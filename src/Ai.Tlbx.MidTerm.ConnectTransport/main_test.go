package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"io"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

func TestReadCommandsRejectsOversizedLinesAndTrailingJSON(t *testing.T) {
	for _, input := range []string{
		strings.Repeat("x", maxLineBytes+1) + "\n",
		`{"type":"init","version":1} {"type":"close"}` + "\n",
		`{"type":"init","version":1,"unexpected":true}` + "\n",
	} {
		commands := make(chan inputResult, 4)
		readCommands(context.Background(), strings.NewReader(input), commands)
		result, ok := <-commands
		if !ok || result.err == nil {
			t.Fatal("invalid command was accepted")
		}
	}
}

func TestInitVersionAndSendBeforeOpen(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	tr := &transport{ctx: ctx, output: make(chan []byte, outputQueueSize), failure: make(chan error, 1)}
	if err := tr.handle(command{Type: "send"}); err == nil {
		t.Fatal("send before init accepted")
	}
	if err := tr.handle(command{Type: "init", Version: 2}); err == nil {
		t.Fatal("unsupported version accepted")
	}
	if err := tr.handle(command{Type: "init", Version: 1}); err != nil {
		t.Fatal(err)
	}
	defer tr.pc.Close()
	if err := tr.handle(command{Type: "init", Version: 1}); err == nil {
		t.Fatal("second init accepted")
	}
	if err := tr.handle(command{Type: "send", Data: "AQ=="}); err == nil {
		t.Fatal("send before data channel open accepted")
	}
	for range maxCandidates {
		if err := tr.handle(command{Type: "candidate", Candidate: "candidate:1 1 udp 1 127.0.0.1 1234 typ host"}); err != nil {
			t.Fatal(err)
		}
	}
	if err := tr.handle(command{Type: "candidate"}); err == nil {
		t.Fatal("unbounded candidate queue accepted")
	}
}

func TestOutputQueueFailsClosed(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	tr := &transport{ctx: ctx, output: make(chan []byte, 1), failure: make(chan error, 1)}
	if !tr.emit(map[string]any{"type": "state"}) || tr.emit(map[string]any{"type": "data", "data": "AQ=="}) {
		t.Fatal("output queue did not enforce its bound")
	}
	select {
	case <-tr.failure:
	default:
		t.Fatal("output overrun did not fail transport")
	}
}

func TestProtocolErrorIsJSONAndDoesNotLeakCredentials(t *testing.T) {
	var output bytes.Buffer
	err := run(strings.NewReader("{\"type\":\"init\",\"version\":2,\"iceServers\":[{\"urls\":[\"turn:user:secret@example.invalid\"]}]}\n"), &output)
	if err == nil {
		t.Fatal("invalid init accepted")
	}
	var event map[string]any
	if err := json.Unmarshal(bytes.TrimSpace(output.Bytes()), &event); err != nil {
		t.Fatalf("output is not one JSON event: %v", err)
	}
	if event["type"] != "error" || strings.Contains(output.String(), "secret") {
		t.Fatalf("invalid or sensitive error event: %s", output.String())
	}
}

func TestRealPeerBinaryRoundtripAndPath(t *testing.T) {
	inputReader, inputWriter := io.Pipe()
	outputReader, outputWriter := io.Pipe()
	done := make(chan error, 1)
	go func() { done <- run(inputReader, outputWriter) }()
	var inputMu sync.Mutex
	send := func(c command) error {
		inputMu.Lock()
		defer inputMu.Unlock()
		return json.NewEncoder(inputWriter).Encode(c)
	}
	t.Cleanup(func() {
		_ = inputWriter.Close()
		_ = inputReader.Close()
		_ = outputReader.Close()
		_ = outputWriter.Close()
		select {
		case <-done:
		case <-time.After(5 * time.Second):
			t.Error("transport did not stop after parent shutdown")
		}
	})

	browser, err := webrtc.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer browser.Close()
	dc, err := browser.CreateDataChannel("tlbx", nil)
	if err != nil {
		t.Fatal(err)
	}
	opened := make(chan struct{}, 1)
	dc.OnOpen(func() { opened <- struct{}{} })
	received := make(chan []byte, 1)
	dc.OnMessage(func(message webrtc.DataChannelMessage) { received <- bytes.Clone(message.Data) })

	events := make(chan map[string]json.RawMessage, 64)
	go func() {
		scanner := bufio.NewScanner(outputReader)
		scanner.Buffer(make([]byte, 4096), maxLineBytes+1)
		for scanner.Scan() {
			var event map[string]json.RawMessage
			if json.Unmarshal(scanner.Bytes(), &event) == nil {
				events <- event
			}
		}
	}()
	if err := send(command{Type: "init", Version: 1}); err != nil {
		t.Fatal(err)
	}
	browser.OnICECandidate(func(candidate *webrtc.ICECandidate) {
		if candidate != nil {
			value := candidate.ToJSON()
			_ = send(command{Type: "candidate", Candidate: value.Candidate, SDPMid: value.SDPMid, SDPMLineIndex: value.SDPMLineIndex})
		}
	})
	offer, err := browser.CreateOffer(nil)
	if err != nil {
		t.Fatal(err)
	}
	if err = browser.SetLocalDescription(offer); err != nil {
		t.Fatal(err)
	}
	if err = send(command{Type: "offer", SDP: offer.SDP}); err != nil {
		t.Fatal(err)
	}

	deadline := time.After(15 * time.Second)
	var pending []webrtc.ICECandidateInit
	var ready, answered, path, channelOpen bool
	for !ready || !answered || !path || !channelOpen {
		select {
		case event := <-events:
			switch eventString(t, event, "type") {
			case "ready":
				ready = true
			case "answer":
				if err = browser.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: eventString(t, event, "sdp")}); err != nil {
					t.Fatal(err)
				}
				answered = true
				for _, candidate := range pending {
					if err = browser.AddICECandidate(candidate); err != nil {
						t.Fatal(err)
					}
				}
				pending = nil
			case "candidate":
				var candidate webrtc.ICECandidateInit
				encoded, _ := json.Marshal(event)
				_ = json.Unmarshal(encoded, &candidate)
				if !answered {
					pending = append(pending, candidate)
				} else if err = browser.AddICECandidate(candidate); err != nil {
					t.Fatal(err)
				}
			case "path":
				path = eventString(t, event, "localType") == "host" && eventString(t, event, "remoteType") == "host" && eventString(t, event, "protocol") == "udp"
			case "open":
				channelOpen = true
			case "error":
				t.Fatalf("transport error: %s", eventString(t, event, "message"))
			}
		case err = <-done:
			t.Fatalf("transport stopped: %v", err)
		case <-deadline:
			t.Fatalf("negotiation timed out: ready=%v answer=%v path=%v open=%v", ready, answered, path, channelOpen)
		}
	}
	select {
	case <-opened:
	case <-deadline:
		t.Fatal("browser peer did not open")
	}

	payload := make([]byte, maxMessageBytes)
	for i := range payload {
		payload[i] = byte(i)
	}
	if err = dc.Send(payload); err != nil {
		t.Fatal(err)
	}
	for {
		select {
		case event := <-events:
			if eventString(t, event, "type") == "data" {
				actual, decodeErr := base64.StdEncoding.DecodeString(eventString(t, event, "data"))
				if decodeErr != nil || !bytes.Equal(actual, payload) {
					t.Fatal("peer-to-parent payload changed")
				}
				goto sendBack
			}
		case <-deadline:
			t.Fatal("peer-to-parent data timed out")
		}
	}

sendBack:
	if err = send(command{Type: "send", Data: base64.StdEncoding.EncodeToString(payload)}); err != nil {
		t.Fatal(err)
	}
	select {
	case actual := <-received:
		if !bytes.Equal(actual, payload) {
			t.Fatal("parent-to-peer payload changed")
		}
	case <-deadline:
		t.Fatal("parent-to-peer data timed out")
	}

	// Exercise a normal large HTTP/file response: frames stay bounded while
	// the transport drains SCTP congestion instead of rejecting a valid stream.
	const bulkBytes = 20 * 1024 * 1024
	const frames = bulkBytes / maxMessageBytes
	bulkDone := make(chan bool, 1)
	go func() {
		for range frames {
			select {
			case actual := <-received:
				if !bytes.Equal(actual, payload) {
					bulkDone <- false
					return
				}
			case <-time.After(20 * time.Second):
				bulkDone <- false
				return
			}
		}
		bulkDone <- true
	}()
	for range frames {
		if err = send(command{Type: "send", Data: base64.StdEncoding.EncodeToString(payload)}); err != nil {
			t.Fatal(err)
		}
	}
	select {
	case ok := <-bulkDone:
		if !ok {
			t.Fatal("20 MiB stream was changed or stalled")
		}
	case <-time.After(20 * time.Second):
		t.Fatal("20 MiB stream timed out")
	}
}

func eventString(t *testing.T, event map[string]json.RawMessage, key string) string {
	t.Helper()
	var value string
	if err := json.Unmarshal(event[key], &value); err != nil {
		t.Fatalf("invalid event field %s: %v", key, err)
	}
	return value
}
