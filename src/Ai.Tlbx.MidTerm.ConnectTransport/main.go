// Copyright (c) tlbx contributors. Licensed under the repository's AGPL-3.0 license.
// This process provides WebRTC transport only. Its parent owns authentication,
// authorization, signaling delivery, and the application protocol.
package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"sync"
	"time"

	"github.com/pion/webrtc/v4"
)

const (
	protocolVersion   = 1
	maxMessageBytes   = 64 * 1024
	maxLineBytes      = 128 * 1024
	maxSDPBytes       = 64 * 1024
	maxCandidateBytes = 8 * 1024
	maxCandidates     = 128
	maxBufferedBytes  = 1024 * 1024
	outputQueueSize   = 32
	sendWaitTimeout   = 30 * time.Second
)

type iceServer struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username,omitempty"`
	Credential string   `json:"credential,omitempty"`
}

type command struct {
	Type          string      `json:"type"`
	Version       int         `json:"version,omitempty"`
	ICEServers    []iceServer `json:"iceServers,omitempty"`
	RelayOnly     bool        `json:"relayOnly,omitempty"`
	SDP           string      `json:"sdp,omitempty"`
	Candidate     string      `json:"candidate,omitempty"`
	SDPMid        *string     `json:"sdpMid,omitempty"`
	SDPMLineIndex *uint16     `json:"sdpMLineIndex,omitempty"`
	Data          string      `json:"data,omitempty"`
}

type inputResult struct {
	command command
	err     error
}

type transport struct {
	ctx         context.Context
	output      chan []byte
	failure     chan error
	inputDone   chan struct{}
	bufferedLow chan struct{}
	pc          *webrtc.PeerConnection
	channel     *webrtc.DataChannel
	mu          sync.Mutex
	pending     []webrtc.ICECandidateInit
	offered     bool
}

func main() {
	if err := run(os.Stdin, os.Stdout); err != nil {
		// Messages deliberately exclude SDP, server URLs, credentials and payloads.
		fmt.Fprintln(os.Stderr, "Connect transport:", err)
		os.Exit(1)
	}
}

func run(input io.Reader, output io.Writer) error {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	t := &transport{
		ctx:         ctx,
		output:      make(chan []byte, outputQueueSize),
		failure:     make(chan error, 1),
		inputDone:   make(chan struct{}),
		bufferedLow: make(chan struct{}, 1),
	}
	defer func() {
		if t.pc != nil {
			_ = t.pc.Close()
		}
	}()

	inputs := make(chan inputResult, 4)
	go func() {
		defer close(t.inputDone)
		readCommands(ctx, input, inputs)
	}()
	writeErrors := make(chan error, 1)
	go func() {
		for {
			select {
			case <-ctx.Done():
				return
			case data := <-t.output:
				if count, err := output.Write(data); err != nil || count != len(data) {
					writeErrors <- errors.New("stdout write failed")
					return
				}
			}
		}
	}()

	for {
		select {
		case err := <-writeErrors:
			return err
		case err := <-t.failure:
			return t.drainBeforeExit(writeErrors, err)
		case next, ok := <-inputs:
			if !ok {
				return nil
			}
			if next.err != nil {
				t.emitError("invalid_command", "Invalid or oversized command.")
				return t.drainBeforeExit(writeErrors, errors.New("invalid command"))
			}
			if next.command.Type == "close" {
				return nil
			}
			if err := t.handle(next.command); err != nil {
				t.emitError("protocol_error", err.Error())
				return t.drainBeforeExit(writeErrors, err)
			}
		}
	}
}

// A bounded grace period permits the error event to reach a draining parent.
// The parent must still enforce a process shutdown deadline for blocked pipes.
func (t *transport) drainBeforeExit(writeErrors <-chan error, result error) error {
	deadline := time.NewTimer(100 * time.Millisecond)
	defer deadline.Stop()
	select {
	case <-deadline.C:
	case <-writeErrors:
	}
	return result
}

func readCommands(ctx context.Context, input io.Reader, commands chan<- inputResult) {
	defer close(commands)
	scanner := bufio.NewScanner(input)
	scanner.Buffer(make([]byte, 4096), maxLineBytes+1)
	for scanner.Scan() {
		var next inputResult
		line := scanner.Bytes()
		if len(line) > maxLineBytes {
			next.err = errors.New("command line too large")
		} else {
			decoder := json.NewDecoder(bytes.NewReader(line))
			decoder.DisallowUnknownFields()
			next.err = decoder.Decode(&next.command)
			if next.err == nil && decoder.Decode(new(any)) != io.EOF {
				next.err = errors.New("trailing JSON")
			}
		}
		select {
		case commands <- next:
		case <-ctx.Done():
			return
		}
		if next.err != nil {
			return
		}
	}
	if scanner.Err() != nil {
		select {
		case commands <- inputResult{err: errors.New("command read failed or line too large")}:
		case <-ctx.Done():
		}
	}
}

func (t *transport) emit(event map[string]any) bool {
	if t.ctx.Err() != nil {
		return false
	}
	data, err := json.Marshal(event)
	if err != nil || len(data) > maxLineBytes {
		t.fail(errors.New("output event exceeds protocol limit"))
		return false
	}
	data = append(data, '\n')
	select {
	case t.output <- data:
		return true
	default:
		t.fail(errors.New("stdout backpressure limit exceeded"))
		return false
	}
}

func (t *transport) emitError(code, message string) {
	t.emit(map[string]any{"type": "error", "code": code, "message": message, "fatal": true})
}

func (t *transport) fail(err error) {
	select {
	case t.failure <- err:
	default:
	}
}

func (t *transport) handle(c command) error {
	if c.Type == "init" {
		return t.initialize(c)
	}
	if t.pc == nil {
		return errors.New("init must be the first command")
	}
	switch c.Type {
	case "offer":
		if t.offered || c.SDP == "" || len(c.SDP) > maxSDPBytes {
			return errors.New("one nonempty offer up to 65536 bytes is required")
		}
		if err := t.pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: c.SDP}); err != nil {
			return errors.New("remote offer rejected")
		}
		t.offered = true
		for _, candidate := range t.pending {
			if err := t.pc.AddICECandidate(candidate); err != nil {
				return errors.New("queued remote candidate rejected")
			}
		}
		t.pending = nil
		answer, err := t.pc.CreateAnswer(nil)
		if err != nil {
			return errors.New("answer creation failed")
		}
		if err = t.pc.SetLocalDescription(answer); err != nil {
			return errors.New("local answer rejected")
		}
		t.emit(map[string]any{"type": "answer", "sdp": answer.SDP})
	case "candidate":
		if len(c.Candidate) > maxCandidateBytes || (c.SDPMid != nil && len(*c.SDPMid) > 256) {
			return errors.New("remote candidate exceeds protocol limit")
		}
		candidate := webrtc.ICECandidateInit{Candidate: c.Candidate, SDPMid: c.SDPMid, SDPMLineIndex: c.SDPMLineIndex}
		if !t.offered {
			if len(t.pending) >= maxCandidates {
				return errors.New("too many candidates before offer")
			}
			t.pending = append(t.pending, candidate)
		} else if err := t.pc.AddICECandidate(candidate); err != nil {
			return errors.New("remote candidate rejected")
		}
	case "send":
		if len(c.Data) > base64.StdEncoding.EncodedLen(maxMessageBytes) {
			return errors.New("binary payload exceeds 65536 bytes")
		}
		data, err := base64.StdEncoding.Strict().DecodeString(c.Data)
		if err != nil || len(data) > maxMessageBytes {
			return errors.New("invalid base64 payload")
		}
		t.mu.Lock()
		dc := t.channel
		t.mu.Unlock()
		if dc == nil || dc.ReadyState() != webrtc.DataChannelStateOpen {
			return errors.New("data channel is not open")
		}
		if err := t.waitForWritable(dc, len(data)); err != nil {
			return err
		}
		if err := dc.Send(data); err != nil {
			return errors.New("data channel send failed")
		}
	default:
		return errors.New("unknown command type")
	}
	return nil
}

// SCTP drains on Pion's network goroutines. Waiting here applies bounded
// backpressure to stdin without blocking incoming peer messages or stdout.
func (t *transport) waitForWritable(dc *webrtc.DataChannel, size int) error {
	deadline := time.NewTimer(sendWaitTimeout)
	defer deadline.Stop()
	for dc.BufferedAmount()+uint64(size) > maxBufferedBytes {
		if dc.ReadyState() != webrtc.DataChannelStateOpen {
			return errors.New("data channel closed during send")
		}
		select {
		case <-t.bufferedLow:
		case <-t.ctx.Done():
			return errors.New("transport stopped during send")
		case <-t.inputDone:
			return errors.New("parent input ended during send")
		case err := <-t.failure:
			return err
		case <-deadline.C:
			return errors.New("data channel backpressure deadline exceeded")
		}
	}
	return nil
}

func (t *transport) initialize(c command) error {
	if t.pc != nil || c.Version != protocolVersion || len(c.ICEServers) > 8 {
		return errors.New("init requires version 1 and at most eight ICE servers, once")
	}
	config := webrtc.Configuration{}
	if c.RelayOnly {
		config.ICETransportPolicy = webrtc.ICETransportPolicyRelay
	}
	for _, server := range c.ICEServers {
		if len(server.URLs) == 0 || len(server.URLs) > 8 || len(server.Username) > 512 || len(server.Credential) > 1024 {
			return errors.New("ICE server configuration exceeds protocol limit")
		}
		for _, url := range server.URLs {
			if len(url) > 2048 {
				return errors.New("ICE server URL exceeds protocol limit")
			}
		}
		config.ICEServers = append(config.ICEServers, webrtc.ICEServer{
			URLs: server.URLs, Username: server.Username,
			Credential: server.Credential, CredentialType: webrtc.ICECredentialTypePassword,
		})
	}
	settings := webrtc.SettingEngine{}
	settings.SetSCTPMaxMessageSize(maxMessageBytes)
	settings.SetSCTPMaxReceiveBufferSize(2 * maxBufferedBytes)
	api := webrtc.NewAPI(webrtc.WithSettingEngine(settings))
	pc, err := api.NewPeerConnection(config)
	if err != nil {
		return errors.New("ICE configuration rejected")
	}
	t.pc = pc
	pc.OnICECandidate(func(candidate *webrtc.ICECandidate) {
		if candidate == nil {
			t.emit(map[string]any{"type": "candidate", "candidate": "", "sdpMid": nil})
			return
		}
		c := candidate.ToJSON()
		t.emit(map[string]any{"type": "candidate", "candidate": c.Candidate, "sdpMid": c.SDPMid, "sdpMLineIndex": c.SDPMLineIndex})
	})
	pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		t.emit(map[string]any{"type": "state", "state": state.String()})
		if state == webrtc.PeerConnectionStateFailed {
			t.fail(errors.New("peer connection failed"))
		}
	})
	pc.SCTP().Transport().ICETransport().OnSelectedCandidatePairChange(func(pair *webrtc.ICECandidatePair) {
		if pair != nil && pair.Local != nil && pair.Remote != nil {
			t.emit(map[string]any{"type": "path", "localType": pair.Local.Typ.String(), "remoteType": pair.Remote.Typ.String(), "protocol": pair.Local.Protocol.String()})
		}
	})
	pc.OnDataChannel(t.acceptChannel)
	t.emit(map[string]any{"type": "ready", "version": protocolVersion})
	return nil
}

func (t *transport) acceptChannel(dc *webrtc.DataChannel) {
	t.mu.Lock()
	valid := t.channel == nil && dc.Label() == "tlbx" && dc.Ordered() && dc.MaxPacketLifeTime() == nil && dc.MaxRetransmits() == nil
	if valid {
		t.channel = dc
	}
	t.mu.Unlock()
	if !valid {
		_ = dc.Close()
		t.emitError("invalid_channel", "Exactly one reliable ordered binary channel named tlbx is allowed.")
		t.fail(errors.New("remote data channel rejected"))
		return
	}
	dc.OnOpen(func() {
		t.emit(map[string]any{"type": "open"})
	})
	dc.SetBufferedAmountLowThreshold(maxBufferedBytes / 2)
	dc.OnBufferedAmountLow(func() {
		select {
		case t.bufferedLow <- struct{}{}:
		default:
		}
	})
	dc.OnClose(func() {
		t.emit(map[string]any{"type": "state", "state": "closed"})
		t.fail(errors.New("data channel closed"))
	})
	dc.OnError(func(_ error) {
		t.emitError("channel_error", "Data channel failed.")
		t.fail(errors.New("data channel failed"))
	})
	dc.OnMessage(func(message webrtc.DataChannelMessage) {
		if message.IsString || len(message.Data) > maxMessageBytes {
			t.emitError("invalid_message", "Binary messages up to 65536 bytes are required.")
			t.fail(errors.New("remote message rejected"))
			return
		}
		t.emit(map[string]any{"type": "data", "data": base64.StdEncoding.EncodeToString(message.Data)})
	})
}
