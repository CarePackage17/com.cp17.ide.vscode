# TODO

- Respect user settings instead of generating all the projects all the time
- Parse stuff from response files and add into the project (nullable, extra files, compiler switches, etc.)
- Clean up code (it's pretty ugly rn)
- Test on Windows
- Test with bigger projects and more assemblies. Maybe Unity's sample stuff on github for starters.
- Support for player projects like VS and Rider
- Check if we can use parallel jobs for the project generation stuff (probably requires UnsafeText, otherwise we can't store it in NativeArray)
- Jobify sln write? (not really necessary from perf viewpoint)
- Unit tests?
- After jobified variant has parity, delete old stuff