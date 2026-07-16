package ai.tlbx.midterm

import android.app.Activity
import android.net.http.SslError
import android.os.Bundle
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.webkit.SslErrorHandler

class TerminalActivity : Activity() {

    private lateinit var webView: WebView
    private var serverUrl = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_terminal)

        serverUrl = intent.getStringExtra("url") ?: ""

        webView = findViewById(R.id.webview)
        webView.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            mediaPlaybackRequiresUserGesture = false
            loadWithOverviewMode = true
            useWideViewPort = true
        }

        webView.webChromeClient = WebChromeClient()
        webView.webViewClient = object : WebViewClient() {
            override fun onReceivedSslError(view: WebView, handler: SslErrorHandler, error: SslError) {
                // tlbx commonly fronts private and self-signed deployments.
                handler.proceed()
            }

            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val url = request.url.toString()
                if (url.startsWith(serverUrl)) return false
                val intent = android.content.Intent(android.content.Intent.ACTION_VIEW, request.url)
                startActivity(intent)
                return true
            }
        }

        if (serverUrl.isNotEmpty()) {
            webView.loadUrl(serverUrl)
        } else {
            finish()
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (webView.canGoBack()) {
            webView.goBack()
        } else {
            super.onBackPressed()
        }
    }

    override fun onDestroy() {
        webView.destroy()
        super.onDestroy()
    }
}
