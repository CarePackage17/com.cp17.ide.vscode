# Installation

- Open package manager UI
- Add from git URL, then paste: `git+https://github.com/CarePackage17/com.unity.ide.vscode?path=/Packages/com.unity.ide.vscode#sdk-style`

# TODO

- ~~Respect user settings instead of generating all the projects all the time~~
- ~~Move cost of string conversion (utf16 to utf8) into job so main thread doesn't drown in work~~
- Parse stuff from response files and add into the project (nullable, extra files, compiler switches, etc.)
- Debug `dotnet build` circular dependency when building sln (wtf?)
- Clean up code (it's pretty ugly rn)
- Test on Windows
- Test with bigger projects and more assemblies. Maybe Unity's sample stuff on github for starters.
- Support for player projects like VS and Rider
- Check if we can use parallel jobs for the project generation stuff (probably requires UnsafeText, otherwise we can't store it in NativeArray)
- ~~Jobify sln write? (not really necessary from perf viewpoint)~~
- Roslyn analyzer support?
- Unit tests?
- After jobified variant has parity, delete old stuff