// Calls only the actual native URL constructors: no PeerConnection, DNS or sockets.
#include <rtc/configuration.hpp>
#include <iostream>
#include <stdexcept>

static const char *stage = "initialization";
int main() try {
    struct Case { const char *name, *url, *host, *user, *password; unsigned short port; };
    const Case cases[] = {
        {"turn-ipv4", "turn:user:pw@127.0.0.1:3478?transport=tcp", "127.0.0.1", "user", "pw", 3478},
        {"turn-ipv6", "turn:user:pw@[::1]:3478?transport=tcp", "::1", "user", "pw", 3478},
        {"turn-ipv6-encoded-credentials", "turn:user%3Aname:pass%40word@[2001:db8::1]:5349?transport=tcp", "2001:db8::1", "user:name", "pass@word", 5349}
    };
    for (const auto &test : cases) {
        stage = test.name;
        rtc::IceServer server(test.url);
        if (server.hostname != test.host || server.port != test.port || server.username != test.user
            || server.password != test.password || server.type != rtc::IceServer::Type::Turn
            || server.relayType != rtc::IceServer::RelayType::TurnTcp)
            throw std::runtime_error("Native ICE URL constructor contract failed");
    }
    stage = "http-proxy-ipv6";
    rtc::ProxyServer proxy("http://[::1]:3128");
    if (proxy.hostname != "::1" || proxy.port != 3128 || proxy.type != rtc::ProxyServer::Type::Http)
        throw std::runtime_error("Native proxy IPv6 constructor contract failed");
    stage = "http-proxy-credentials";
    const std::string proxyUser(32, 'a'), proxyPassword(64, 'b');
    rtc::ProxyServer authenticatedProxy("http://" + proxyUser + ":" + proxyPassword + "@127.0.0.1:49123");
    if (authenticatedProxy.hostname != "127.0.0.1" || authenticatedProxy.port != 49123
        || authenticatedProxy.type != rtc::ProxyServer::Type::Http
        || authenticatedProxy.username != proxyUser || authenticatedProxy.password != proxyPassword)
        throw std::runtime_error("Native authenticated HTTP proxy constructor contract failed");
    std::cout << "PASS: actual native IceServer/ProxyServer constructors preserve IPv4/IPv6, credentials and TCP projection; no peer or socket created.\n";
} catch (const std::exception &error) {
    // Only hard-coded public test fixtures are passed to these constructors.
    std::cerr << "Native URL constructor proof failed at " << stage << ": " << error.what() << '\n';
    return 1;
} catch (...) {
    std::cerr << "Native URL constructor proof failed at " << stage << ".\n";
    return 1;
}
