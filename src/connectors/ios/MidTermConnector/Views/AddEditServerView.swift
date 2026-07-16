import SwiftUI

struct LauncherHintSection: View {
    let lastConnected: Date?

    var body: some View {
        Section("How This App Works") {
            Text("Use tlbx Hub after you connect instead of keeping a list of saved servers in the app.")
                .foregroundStyle(.secondary)
            Text("The last address stays on this device so the same start screen is ready next time.")
                .foregroundStyle(.secondary)
            if let lastConnected, lastConnected > .distantPast {
                Text("Last opened \(lastConnected.formatted(date: .abbreviated, time: .shortened))")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
        }
    }
}
