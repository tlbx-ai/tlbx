import SwiftUI
import UIKit
import WebKit

struct TerminalView: View {
    let server: Server
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        WebViewContainer(url: server.url)
            .ignoresSafeArea()
            .overlay(alignment: .topLeading) {
                Button { dismiss() } label: {
                    Image(systemName: "chevron.left.circle.fill")
                        .font(.title)
                        .foregroundStyle(.white.opacity(0.7))
                        .padding(12)
                }
            }
    }
}

struct WebViewContainer: UIViewRepresentable {
    let url: String

    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.allowsInlineMediaPlayback = true
        config.mediaTypesRequiringUserActionForPlayback = []

        let webView = WKWebView(frame: .zero, configuration: config)
        webView.navigationDelegate = context.coordinator
        webView.allowsBackForwardNavigationGestures = true
        webView.scrollView.contentInsetAdjustmentBehavior = .never

        if let serverUrl = URL(string: url) {
            webView.load(URLRequest(url: serverUrl))
        }
        return webView
    }

    func updateUIView(_ uiView: WKWebView, context: Context) {}

    func makeCoordinator() -> Coordinator {
        Coordinator(parent: self)
    }

    final class Coordinator: NSObject, WKNavigationDelegate {
        let parent: WebViewContainer

        init(parent: WebViewContainer) {
            self.parent = parent
        }

        func webView(_ webView: WKWebView, didReceive challenge: URLAuthenticationChallenge,
                     completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
            guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
                  let trust = challenge.protectionSpace.serverTrust else {
                completionHandler(.performDefaultHandling, nil)
                return
            }

            // tlbx commonly fronts private and self-signed deployments.
            completionHandler(.useCredential, URLCredential(trust: trust))
        }

        func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                     decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            if let targetUrl = navigationAction.request.url,
               navigationAction.navigationType == .linkActivated,
               !targetUrl.absoluteString.hasPrefix(parent.url) {
                UIApplication.shared.open(targetUrl)
                decisionHandler(.cancel)
                return
            }
            decisionHandler(.allow)
        }
    }
}
