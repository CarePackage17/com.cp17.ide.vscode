# Installation

- Open package manager UI
- Add from git URL, then paste: `git+https://github.com/CarePackage17/com.unity.ide.vscode?path=/Packages/com.cp17.ide.vscode#sdk-style`

# TODO

- ~~Respect user settings instead of generating all the projects all the time~~
- ~~Move cost of string conversion (utf16 to utf8) into job so main thread doesn't drown in work~~
- ~~Parse stuff from response files and add into the project (nullable, extra files, compiler switches, etc.)~~
- Debug `dotnet build` circular dependency when building sln (wtf?)
  - And some new bugs with player projects too... :/
- Figure out how `SyncIfNeeded` is supposed to work because it doesn't look like Unity tells us when a source file is added/deleted, oof
  - Maybe a linux bug, on Windows it does tell me at least in the added/deleted cases
- Clean up code (it's pretty ugly rn)
- ~~Test on Windows~~
  - Works after fixing some of the path separator BS, but should probably have regression tests at least
- Test with bigger projects and more assemblies. Maybe Unity's sample stuff on github for starters.
  - Also test on shitty hardware
  - URP template and boss room (https://github.com/Unity-Technologies/com.unity.multiplayer.samples.coop/releases) work fine so far
- ~~Support for player projects like VS and Rider~~
- Settings button for easily enabling debug logging (adding ifdef behind the scenes somewhere)
- Check if we can use parallel jobs for the project generation stuff (probably requires UnsafeText, otherwise we can't store it in NativeArray)
- ~~Jobify sln write? (not really necessary from perf viewpoint)~~
- Roslyn analyzer support?
- Unit tests?
- After jobified variant has parity, delete old stuff
- ~~Write our own vscode settings file like the new VS extension does~~
- ~~Handle symlinks on Linux so installations are not duplicated (Mono.Posix go brrr)~~