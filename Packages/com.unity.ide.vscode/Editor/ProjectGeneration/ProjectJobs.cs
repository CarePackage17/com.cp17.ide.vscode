using Unity.Jobs;
using Unity.Collections;

struct GenerateProjectJob : IJob
{
    //we need some data here, probably using the collections package
    //and nativetext.
    [ReadOnly]
    public NativeText template;
    [ReadOnly]
    public FixedString32Bytes langVersion;
    [ReadOnly]
    public bool unsafeCode;
    [ReadOnly]
    public NativeText asmSearchPath;

    [ReadOnly]
    public NativeText defines;

    public NativeText output;

    public void Execute()
    {
        FixedString32Bytes allowUnsafe = new(unsafeCode.ToString());
        output.AppendFormat(in template, langVersion, defines, allowUnsafe, asmSearchPath);
    }
}
