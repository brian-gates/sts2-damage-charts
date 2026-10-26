// Prints the CoreGraphics window number of Slay the Spire 2's main window, for `screencapture -l`.
import CoreGraphics

let windows = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] ?? []
for w in windows where (w["kCGWindowOwnerName"] as? String) == "Slay the Spire 2"
    && (w["kCGWindowName"] as? String) == "Slay the Spire 2" {
    print(w["kCGWindowNumber"]!)
    break
}
