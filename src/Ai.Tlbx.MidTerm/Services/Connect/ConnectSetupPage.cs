namespace Ai.Tlbx.MidTerm.Services.Connect;

internal static class ConnectSetupPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Connect — tlbx</title><link rel="stylesheet" href="/connect-setup.css"><script src="/connect-setup.js" defer></script></head>
        <body><main><a class="back" href="/">← tlbx</a><h1>Connect this computer</h1>
        <p>Generate your connection files here, on this computer. Confirm your installation password to continue.</p>
        <div id="status" role="status" aria-live="polite">Loading connection status…</div>
        <form id="unlock"><label for="password">Installation password</label><input id="password" type="password" autocomplete="current-password" maxlength="1024" required>
        <button type="submit">Unlock Connect setup</button></form>
        <form id="generate" hidden><label for="name">Computer name</label><input id="name" maxlength="100" autocomplete="off" required>
        <label for="server">Connect server</label><input id="server" type="url" value="https://connect.tlbx.ai" autocomplete="off" required>
        <p id="replacement" hidden>Generating replacement keys disconnects current browsers and gives this computer a new identity. Import the new files to reconnect.</p>
        <button id="generate-button" type="submit">Generate connection files</button>
        <button id="disconnect" type="button" class="danger" hidden>Remove this computer's Connect keys</button></form>
        <section id="downloads" hidden><h2>Save both files</h2>
        <p>Import the public registration file in Connect. Open the private browser access file there when you want to use this computer.</p>
        <button id="download-registration" type="button">Download public registration</button>
        <button id="download-access" type="button">Download private browser access</button>
        <p class="warning">The private access file grants access to this computer. Keep it private. This page forgets the downloads after two minutes.</p></section>
        <p class="detail">Setup is available only in a browser connected directly to localhost. Your host private key stays in this installation's protected secret storage.
        <a href="/connect-licenses.txt">Transport licenses</a></p></main></body></html>
        """;

    public const string Css = """
        :root{color-scheme:dark;font-family:Inter,ui-sans-serif,system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#07080b;color:#f3f5f7}
        body{margin:0;padding:32px 20px}main{max-width:580px;margin:4vh auto}h1{font-size:30px;font-weight:700;letter-spacing:-.045em;margin-top:36px}
        p{color:#a4a9b5;line-height:1.6}a{color:#41d5ff}a:hover{color:#a7ecff}label{display:block;margin:18px 0 8px}form{margin:28px 0}
        input{display:block;box-sizing:border-box;width:100%;background:#101218;color:inherit;border:1px solid #282d38;padding:12px;font:inherit;min-height:48px}
        button{display:inline-block;border:1px solid transparent;border-radius:0;background:#41d5ff;color:#041015;padding:12px 18px;font:inherit;font-weight:700;cursor:pointer;margin:14px 8px 0 0;min-height:48px;box-sizing:border-box;max-width:100%}
        button:hover{background:#a7ecff}button:disabled{opacity:.5;cursor:wait}.danger{background:#101218;color:#f3f5f7;border-color:#282d38}.danger:hover{background:#282d38}.warning{color:#f3f5f7}
        #status{padding:16px 0;border-block:1px solid #282d38;line-height:1.5}section{border-top:1px solid #282d38;padding-top:10px}.detail{font-size:13px;margin-top:36px}
        [hidden]{display:none!important}:focus-visible{outline:3px solid #41d5ff;outline-offset:3px}
        """;

    public const string JavaScript = """
        'use strict';
        const status = document.querySelector('#status'), unlock = document.querySelector('#unlock'), form = document.querySelector('#generate');
        const password = document.querySelector('#password'), downloads = document.querySelector('#downloads');
        let unlockToken = null, files = null, unlockTimer, fileTimer, stopped = false;
        function lock() { unlockToken = null; form.hidden = true; unlock.hidden = false; password.value = ''; clearTimeout(unlockTimer); }
        function forgetFiles() { files = null; downloads.hidden = true; clearTimeout(fileTimer); }
        async function request(path, body) {
          const response = await fetch(path, {method:body === undefined ? 'GET' : 'POST', cache:'no-store',
            headers:body === undefined ? {} : {'Content-Type':'application/json'}, body:body === undefined ? undefined : JSON.stringify(body)});
          if (!response.ok) { let problem; try { problem = await response.json(); } catch {} throw new Error(problem?.detail || (response.status === 401 ? 'Sign in locally and confirm your installation password.' : 'The request failed. Try again locally.')); }
          return response.status === 204 ? null : response.json();
        }
        function show(state) {
          status.textContent = state.lastError || (state.online ? 'Connected to ' + new URL(state.serverUrl).host : state.configured ? 'Keys saved. Waiting for Connect…' : 'This computer has no Connect keys.');
          if (state.name) document.querySelector('#name').value = state.name;
          if (state.serverUrl) document.querySelector('#server').value = state.serverUrl;
          document.querySelector('#disconnect').hidden = !state.configured;
          document.querySelector('#replacement').hidden = !state.configured;
          document.querySelector('#generate-button').textContent = state.configured ? 'Generate replacement connection files' : 'Generate connection files';
        }
        unlock.addEventListener('submit', async event => {
          event.preventDefault(); const button = unlock.querySelector('button'); button.disabled = true;
          try { const grant = await request('/api/connect/unlock', {password:password.value}); unlockToken = grant.unlockToken;
            unlock.hidden = true; form.hidden = false; unlockTimer = setTimeout(lock, grant.expiresIn * 1000); document.querySelector('#name').focus();
          } catch(error) { lock(); status.textContent = error.message; } finally { password.value = ''; button.disabled = false; }
        });
        form.addEventListener('submit', async event => {
          event.preventDefault(); const button = document.querySelector('#generate-button'); button.disabled = true; forgetFiles();
          const token = unlockToken;
          try { files = await request('/api/connect/generate', {unlockToken:token, name:document.querySelector('#name').value.trim(), serverUrl:document.querySelector('#server').value.trim()});
            downloads.hidden = false; fileTimer = setTimeout(forgetFiles, 120000); show(await request('/api/connect/status'));
          } catch(error) { status.textContent = error.message; } finally { lock(); button.disabled = false; }
        });
        function download(value, suffix) {
          const blob = new Blob([JSON.stringify(value, null, 2)], {type:'application/json'}), url = URL.createObjectURL(blob), link = document.createElement('a');
          link.href = url; link.download = 'tlbx-connect-' + suffix + '.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
        document.querySelector('#download-registration').addEventListener('click', () => { if (files) download(files.registration, 'registration'); });
        document.querySelector('#download-access').addEventListener('click', () => { if (files) download(files.accessKey, 'access'); });
        document.querySelector('#disconnect').addEventListener('click', async () => {
          const button = document.querySelector('#disconnect'), token = unlockToken; button.disabled = true;
          try { await request('/api/connect/disconnect', {unlockToken:token}); forgetFiles(); show(await request('/api/connect/status')); }
          catch(error) { status.textContent = error.message; } finally { lock(); button.disabled = false; }
        });
        window.addEventListener('pagehide', () => { stopped = true; lock(); forgetFiles(); });
        request('/api/connect/status').then(state => { if (!stopped) show(state); }).catch(error => { if (!stopped) status.textContent = error.message; });
        """;
}
