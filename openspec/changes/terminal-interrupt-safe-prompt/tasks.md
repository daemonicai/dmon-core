## 1. InputReader — expose buffer snapshot

- [x] 1.1 Add `public string CurrentBuffer => _buffer.ToString();` property to `InputReader`
- [x] 1.2 Build `Dmon.Terminal` without warnings

## 2. TerminalRenderer — interrupt-safe prompt

- [x] 2.1 Add `Func<string>? _getBuffer` field and optional constructor parameter
- [x] 2.2 Add `bool _promptActive` and `int _promptBlockLines` fields
- [x] 2.3 Add `private void InterruptPrompt()`: clear `❯` line + erase `_promptBlockLines` lines upward; set `_promptActive = false`
- [x] 2.4 Flip `PrintPrompt()` layout to chrome-above-cursor (blank → top rule → status → bottom rule → `❯ `); no ANSI repositioning; set `_promptActive = true` and `_promptBlockLines`
- [x] 2.5 Add `private void RepromptIfActive()`: calls `PrintPrompt()` + re-echoes `_getBuffer?.Invoke()`; called at the end of write methods that can fire mid-input
- [x] 2.6 Prefix all write methods (`AddSystemLine`, `AddUserLine`, `PrintSeparator`, `AppendToken`, `SettleTurn`) with `InterruptPrompt()`; suffix with `RepromptIfActive()` where appropriate (exclude paths that call `PrintPrompt()` explicitly)
- [x] 2.7 Build `Dmon.Terminal` without warnings

## 3. Program.cs — wire buffer delegate

- [ ] 3.1 Pass `() => inputReader.CurrentBuffer` as the `getBuffer` argument to `TerminalRenderer`
- [ ] 3.2 Build `Dmon.Terminal` without warnings

## 4. Update standing spec

- [ ] 4.1 Update `openspec/changes/terminal-interrupt-safe-prompt/specs/terminal-ui/spec.md` to reflect flipped layout and interrupt behaviour
