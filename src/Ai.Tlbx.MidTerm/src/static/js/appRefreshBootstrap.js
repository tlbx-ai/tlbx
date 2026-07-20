(function () {
  try {
    if (sessionStorage.getItem('midterm.pendingAppRefresh') === '1') {
      document.documentElement.classList.add('tlbx-app-refreshing');
    }
  } catch {
    // Ignore sessionStorage failures and continue with the normal shell.
  }
})();
