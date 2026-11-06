"""Check the patched URL regex with libc++ only; no native runtime or sockets."""
import argparse
from pathlib import Path
import re
import subprocess
import sys
import tempfile


def source_from_patch(patch):
    literals = [line[1:] for line in patch.read_text(encoding="utf-8").splitlines()
                if line.startswith("+") and re.fullmatch(r'\s*R"\(.*\)";\s*', line[1:])]
    if len(literals) != 1:
        raise ValueError("Expected exactly one added raw URL regex literal")
    return r'''#include <array>
#include <iostream>
#include <regex>
#include <string>

int fail(const char *name, const std::string &field) {
    std::cerr << "FAIL: case=" << name << " field=" << field << '\n';
    return 1;
}

int main() {
    const char *currentCase = "pattern";
    try {
        const char *pattern =
@@PATTERN@@
        const std::regex expression(pattern, std::regex::extended);
        if (expression.mark_count() != 17) return fail(currentCase, "capture-count");
        struct Case {
            const char *name;
            std::string url;
            std::array<std::string, 6> expected;
        };
        const std::string user(32, 'a'), password(64, 'b');
        const std::array<Case, 4> cases{{
            {"turn-ipv4", "turn:user:pw@127.0.0.1:3478?transport=tcp",
                {"turn", "user", "pw", "127.0.0.1", "3478", "transport=tcp"}},
            {"turn-ipv6", "turn:user:pw@[::1]:3478?transport=tcp",
                {"turn", "user", "pw", "[::1]", "3478", "transport=tcp"}},
            {"turn-encoded-auth", "turn:user%3Aname:pass%40word@[2001:db8::1]:5349?transport=tcp",
                {"turn", "user%3Aname", "pass%40word", "[2001:db8::1]", "5349", "transport=tcp"}},
            {"http-proxy-auth", "http://" + user + ":" + password + "@127.0.0.1:49123",
                {"http", user, password, "127.0.0.1", "49123", ""}}
        }};
        const std::array<unsigned, 6> groups{2, 6, 8, 10, 12, 15};
        for (const auto &test : cases) {
            currentCase = test.name;
            std::smatch match;
            if (!std::regex_match(test.url, match, expression)) return fail(currentCase, "match");
            if (match.size() != 18) return fail(currentCase, "match-count");
            for (std::size_t field = 0; field < groups.size(); ++field)
                if (match[groups[field]].str() != test.expected[field])
                    return fail(currentCase, "group-" + std::to_string(groups[field]));
            std::cout << "PASS: case=" << currentCase << '\n';
        }
        return 0;
    } catch (const std::regex_error &error) {
        return fail(currentCase, "regex-code-" + std::to_string(static_cast<int>(error.code())));
    } catch (...) {
        return fail(currentCase, "exception");
    }
}
'''.replace("@@PATTERN@@", literals[0])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compiler", default="clang++")
    arguments = parser.parse_args()
    phase = "pattern"
    try:
        patch = Path(__file__).resolve().parents[1] / "ports/libdatachannel/ipv6-url.patch"
        source = source_from_patch(patch)
        with tempfile.TemporaryDirectory(prefix="tlbx-url-regex-") as directory:
            source_path = Path(directory) / "regex.cpp"
            executable = Path(directory) / "regex-probe"
            source_path.write_text(source, encoding="utf-8")
            phase = "compile"
            subprocess.run([arguments.compiler, "-std=c++17", "-stdlib=libc++", str(source_path),
                            "-o", str(executable)], check=True, timeout=60)
            phase = "execute"
            subprocess.run([str(executable)], check=True, timeout=10)
        return 0
    except (OSError, ValueError, subprocess.SubprocessError):
        print(f"FAIL: case={phase} field=probe", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
