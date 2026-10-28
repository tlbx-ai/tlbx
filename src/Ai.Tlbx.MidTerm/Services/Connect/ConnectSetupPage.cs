namespace Ai.Tlbx.MidTerm.Services.Connect;

internal static class ConnectSetupPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Connect — tlbx</title><link rel="stylesheet" href="/connect-setup.css"><script src="/connect-setup.js" defer></script></head>
        <body><main><a class="back" href="/">← tlbx</a><h1>Connect this computer</h1>
        <p>Access this tlbx installation from your browser through tlbx Connect.</p>
        <div id="status" role="status" aria-live="polite">Loading connection status…</div>
        <form id="pair"><label for="name">Computer name</label><input id="name" name="name" maxlength="100" autocomplete="off" required>
        <button type="submit">Pair with Connect</button></form>
        <section id="pending" hidden><h2>Approve this computer</h2><p>Open Connect and enter this code:</p><output id="code"></output>
        <a id="verify" class="button" target="_blank" rel="noopener noreferrer">Open Connect</a><p id="expiry"></p></section>
        <button id="disconnect" class="danger" hidden>Disconnect this computer</button>
        <p class="detail">The connection starts from this computer. Direct connections are preferred; a relay is used when required by the network.
        <a href="/connect-licenses.txt">Transport licenses</a></p>
        </main></body></html>
        """;

    public const string Css = """
        :root{color-scheme:dark;font-family:system-ui,sans-serif;background:#0d0e14;color:#e7e9f0}
        body{margin:0;padding:32px 20px}main{max-width:580px;margin:4vh auto}h1{font-size:30px;letter-spacing:-.02em;margin-top:36px}
        p{color:#b0b5c4;line-height:1.6}a{color:#88adff}label{display:block;margin-bottom:8px}form{margin:28px 0}
        input{display:block;box-sizing:border-box;width:100%;background:#171a24;color:inherit;border:1px solid #434b62;padding:12px;font:inherit;border-radius:6px}
        button,.button{display:inline-block;border:0;border-radius:6px;background:#719bff;color:#0b1222;padding:12px 18px;font:inherit;font-weight:600;cursor:pointer;text-decoration:none;margin-top:14px;min-height:44px;box-sizing:border-box}
        button:disabled{opacity:.5;cursor:wait}.danger{background:transparent;color:#ffb3b3;border:1px solid #765050}
        #status{padding:16px 0;border-block:1px solid #303543;line-height:1.5}section{border-top:1px solid #303543;padding-top:10px}
        output{display:block;font:700 32px ui-monospace,monospace;letter-spacing:.12em;margin:20px 0}.detail{font-size:13px;margin-top:36px}
        [hidden]{display:none!important}:focus-visible{outline:3px solid #b9ccff;outline-offset:3px}
        """;

    public const string JavaScript = """
        'use strict';
        const status = document.querySelector('#status');
        const form = document.querySelector('#pair');
        const pending = document.querySelector('#pending');
        const nameInput = document.querySelector('#name');
        const disconnect = document.querySelector('#disconnect');
        let stopped = false;
        async function request(path, body) {
          const response = await fetch(path, {method: body === undefined ? 'GET' : 'POST',
            headers: body === undefined ? {} : {'Content-Type':'application/json'},
            body: body === undefined ? undefined : JSON.stringify(body)});
          if (response.status === 401) { status.textContent = 'Your local login expired. Open tlbx, sign in, then return here.'; stopped = true; throw new Error('Sign in to tlbx first.'); }
          if (!response.ok) { let problem; try { problem = await response.json(); } catch {} throw new Error(problem?.detail || 'The request failed. Please try again.'); }
          return response.status === 204 ? null : response.json();
        }
        function show(state) {
          status.textContent = state.lastError || (state.online ? 'Connected to ' + new URL(state.serverUrl).host : state.paired ? 'Paired. Connecting…' : 'This computer is not paired.');
          if (!nameInput.value && state.name) nameInput.value = state.name;
          disconnect.hidden = !state.paired && !state.pending;
          pending.hidden = !state.pending;
          if (state.pending) {
            document.querySelector('#code').textContent = state.pending.userCode;
            document.querySelector('#verify').href = state.pending.verificationUri;
            document.querySelector('#expiry').textContent = 'Code expires in ' + Math.ceil(state.pending.expiresIn / 60) + ' minutes.';
          }
        }
        async function refresh() {
          if (stopped) return;
          try { show(await request('/api/connect/status')); } catch (error) { status.textContent = error.message; }
          if (!stopped) setTimeout(refresh, document.hidden ? 10000 : 3000);
        }
        form.addEventListener('submit', async event => {
          event.preventDefault(); const button = form.querySelector('button'); button.disabled = true;
          try { const grant = await request('/api/connect/pair', {name:nameInput.value.trim()});
            show({paired:false,online:false,pending:grant}); status.textContent = 'Waiting for approval in Connect…';
          } catch(error) { status.textContent = error.message; } finally { button.disabled = false; }
        });
        disconnect.addEventListener('click', async () => {
          disconnect.disabled = true;
          try { await request('/api/connect/disconnect', {}); show({paired:false,online:false}); }
          catch(error) { status.textContent = error.message; } finally { disconnect.disabled = false; }
        });
        window.addEventListener('pagehide', () => { stopped = true; });
        refresh();
        """;
}
