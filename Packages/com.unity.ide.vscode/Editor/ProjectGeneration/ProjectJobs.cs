using Unity.Jobs;
using Unity.Collections;

struct GenerateProjectJob : IJob
{
    //we need some data here, probably using the collections package
    //and nativetext.
    // public NativeText template;
    // public FixedString32Bytes langVersion;
    // public NativeText defines;
    // public bool unsafeCode;
    // public NativeText asmSearchPath;

    [ReadOnly]
    public NativeText definesFormat;
    [ReadOnly]
    public NativeText defines;

    public NativeText output;

    public void Execute()
    {
        output.AppendFormat(in definesFormat, defines);
    }
}
