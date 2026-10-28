namespace Ai.Tlbx.MidTerm.Services.Connect;

internal static class ConnectSetupPage
{
    public const string Html = """
        <section id="connect-setup" class="settings-section">
          <h3>tlbx Connect</h3>
          <p class="setting-hint">Use this computer from another browser. Create your connection files here on localhost.</p>
          <p id="connect-setup-status" class="setting-hint" role="status" aria-live="polite">Loading connection status…</p>
          <form id="connect-setup-unlock">
            <div class="setting-row"><label for="connect-setup-password">Installation password</label>
              <input id="connect-setup-password" type="password" autocomplete="current-password" maxlength="1024" required></div>
            <div class="connect-setup-actions"><button type="submit" class="btn-secondary">Unlock key setup</button></div>
          </form>
          <form id="connect-setup-generate" hidden>
            <div class="setting-row"><label for="connect-setup-name">Computer name</label><input id="connect-setup-name" type="text" maxlength="100" autocomplete="off" required></div>
            <div class="setting-row"><label for="connect-setup-server">Connect server</label><input id="connect-setup-server" type="url" value="https://connect.tlbx.ai" autocomplete="off" required></div>
            <p id="connect-setup-replacement" class="setting-hint" hidden>Replacement keys disconnect current browsers. Import the new files to reconnect.</p>
            <div class="connect-setup-actions"><button id="connect-setup-generate-button" type="submit" class="btn-primary">Generate connection files</button>
              <button id="connect-setup-disconnect" type="button" class="btn-secondary" hidden>Remove Connect keys</button></div>
          </form>
          <div id="connect-setup-downloads" hidden>
            <div class="setting-row"><label>Public registration</label><button id="connect-setup-download-registration" type="button" class="btn-secondary">Save registration file</button></div>
            <div class="setting-row"><label>Private browser access</label><button id="connect-setup-download-access" type="button" class="btn-secondary">Save private access file</button></div>
            <p class="setting-hint">Import the registration at connect.tlbx.ai, then import the private access file in your other browser. Keep the private file safe.</p>
            <p class="setting-hint">Downloads are available for two minutes. Leaving Settings clears them.</p>
          </div>
          <p class="setting-hint">Local setup only. The host private key stays in this installation's protected storage. <a href="/connect-licenses.txt" target="_blank" rel="noopener">Transport licenses</a></p>
        </section>
        """;

    public const string JavaScript = """
        'use strict';
        (() => {
        const root = document.querySelector('#connect-setup');
        const find = id => root.querySelector('#connect-setup-' + id);
        const status = find('status'), unlock = find('unlock'), form = find('generate');
        const password = find('password'), downloads = find('downloads');
        let unlockToken = null, files = null, unlockTimer, fileTimer, stopped = false, state, generation = 0;
        function lock() { unlockToken = null; form.hidden = true; unlock.hidden = false; password.value = ''; clearTimeout(unlockTimer); }
        function forgetFiles() { files = null; downloads.hidden = true; clearTimeout(fileTimer); }
        async function request(path, body) {
          const response = await fetch(path, {method:body === undefined ? 'GET' : 'POST', cache:'no-store',
            headers:body === undefined ? {} : {'Content-Type':'application/json'}, body:body === undefined ? undefined : JSON.stringify(body)});
          if (!response.ok) { let problem; try { problem = await response.json(); } catch {} throw new Error(problem?.detail || (response.status === 401 ? 'Sign in locally and confirm your installation password.' : 'The request failed. Try again locally.')); }
          return response.status === 204 ? null : response.json();
        }
        function show(next) {
          state = next;
          status.textContent = state.lastError || (state.online ? 'Connected to ' + new URL(state.serverUrl).host : state.configured ? 'Keys saved. Waiting for Connect…' : 'This computer has no Connect keys.');
          if (state.name) find('name').value = state.name;
          if (state.serverUrl) find('server').value = state.serverUrl;
          find('disconnect').hidden = !state.configured;
          find('replacement').hidden = !state.configured;
          find('generate-button').textContent = state.configured ? 'Generate replacement connection files' : 'Generate connection files';
        }
        unlock.addEventListener('submit', async event => {
          event.preventDefault(); const button = unlock.querySelector('button'); button.disabled = true;
          const attempt = generation;
          try { const grant = await request('/api/connect/unlock', {password:password.value}); if (attempt !== generation) return; unlockToken = grant.unlockToken;
            if (state) show(state);
            unlock.hidden = true; form.hidden = false; unlockTimer = setTimeout(lock, grant.expiresIn * 1000); find('name').focus();
          } catch(error) { if (attempt === generation) { lock(); status.textContent = error.message; } }
          finally { if (attempt === generation) { password.value = ''; button.disabled = false; } }
        });
        form.addEventListener('submit', async event => {
          event.preventDefault(); const button = find('generate-button'); button.disabled = true; forgetFiles();
          const token = unlockToken, attempt = generation;
          try { const generated = await request('/api/connect/generate', {unlockToken:token, name:find('name').value.trim(), serverUrl:find('server').value.trim()});
            if (attempt !== generation) return; files = generated;
            downloads.hidden = false; fileTimer = setTimeout(forgetFiles, 120000); show(await request('/api/connect/status'));
          } catch(error) { if (attempt === generation) status.textContent = error.message; }
          finally { if (attempt === generation) { lock(); button.disabled = false; } }
        });
        function download(value, suffix) {
          const blob = new Blob([JSON.stringify(value, null, 2)], {type:'application/json'}), url = URL.createObjectURL(blob), link = document.createElement('a');
          link.href = url; link.download = 'tlbx-connect-' + suffix + '.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
        find('download-registration').addEventListener('click', () => { if (files) download(files.registration, 'registration'); });
        find('download-access').addEventListener('click', () => { if (files) download(files.accessKey, 'access'); });
        find('disconnect').addEventListener('click', async () => {
          const button = find('disconnect'), token = unlockToken, attempt = generation; button.disabled = true;
          try { await request('/api/connect/disconnect', {unlockToken:token}); if (attempt !== generation) return; forgetFiles(); show(await request('/api/connect/status')); }
          catch(error) { if (attempt === generation) status.textContent = error.message; }
          finally { if (attempt === generation) { lock(); button.disabled = false; } }
        });
        window.addEventListener('tlbx-connect-setup-close', () => {
          generation++; lock(); forgetFiles();
          for (const button of root.querySelectorAll('button')) button.disabled = false;
        });
        window.addEventListener('tlbx-connect-setup-open', () => {
          const attempt = generation;
          request('/api/connect/status').then(next => { if (attempt === generation) show(next); }).catch(error => { if (attempt === generation) status.textContent = error.message; });
        });
        window.addEventListener('pagehide', () => { stopped = true; generation++; lock(); forgetFiles(); });
        request('/api/connect/status').then(state => { if (!stopped) show(state); }).catch(error => { if (!stopped) status.textContent = error.message; });
        })();
        """;
}
