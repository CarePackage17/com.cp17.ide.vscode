using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;

[BurstCompile]
struct GenerateProjectJob : IJob
{
    [ReadOnly] public FixedString4096Bytes assemblyName;
    [ReadOnly] public FixedString64Bytes langVersion;
    [ReadOnly] public FixedString32Bytes unsafeCode;
    [ReadOnly] public NativeText defines;
    [ReadOnly] public NativeText files;
    [ReadOnly] public NativeText assemblySearchPaths;
    [ReadOnly] public NativeArray<FixedString4096Bytes> assemblyReferences;
    [ReadOnly] public NativeArray<FixedString4096Bytes> projectReferences;
    [ReadOnly] public FixedString64Bytes definesFormatString;
    [ReadOnly] public FixedString64Bytes compileFormatString;
    [ReadOnly] public FixedString64Bytes referenceFormatString;
    [ReadOnly] public FixedString64Bytes itemGroupFormatString;
    [ReadOnly] public FixedString64Bytes projectFormatString;
    [ReadOnly] public FixedString4096Bytes propertyGroupFormatString;

    //These little strings are kinda ugly, but even though burst docs say they support
    //initializing those from string literals, that doesn't seem to be the case here.
    //Maybe collections 2.0 only? Anyway, that's not in scope for 2021 LTS.
    [ReadOnly] public FixedString64Bytes projectElement;
    [ReadOnly] public FixedString64Bytes projectEndElement;
    [ReadOnly] public FixedString32Bytes itemGroupElement;
    [ReadOnly] public FixedString32Bytes itemGroupEndElement;

    public NativeText output;

    public void Execute()
    {
        //write all cs files into <Compile /> items
        int colonIndex = -1;
        int startIndex = 0;
        NativeText compileItemsXml = new(Allocator.Temp);

        //I wish NativeText.IndexOf had Span support instead of fucking around with pointers
        System.Span<byte> colon = stackalloc byte[1] { (byte)':' };

        unsafe
        {
            fixed (byte* colonPtr = colon)
            {
                colonIndex = files.IndexOf(colonPtr, colon.Length, startIndex);
            }
        }

        while (colonIndex != -1)
        {
            //get start/end indices
            int length = colonIndex - startIndex;
            NativeText tmp = new(length, Allocator.Temp);

            //2.1 supports Substring, but we can't update if we want to target 2021 LTS...
            //https://github.com/needle-mirror/com.unity.collections/blob/2.1.1/Unity.Collections/FixedStringMethods.cs#L43
            //unsafe copy to tmp string that has the right length
            unsafe { tmp.Append(files.GetUnsafePtr() + startIndex, length); }

            compileItemsXml.AppendFormat(compileFormatString, tmp);

            startIndex = colonIndex + 1;
            unsafe
            {
                fixed (byte* colonPtr = colon)
                {
                    colonIndex = files.IndexOf(colonPtr, colon.Length, startIndex);
                }
            }
        }

        //write all refs into <Include /> items
        NativeText referenceItems = new(Allocator.Temp);
        for (int i = 0; i < assemblyReferences.Length; i++)
        {
            referenceItems.AppendFormat(referenceFormatString, assemblyReferences[i]);
        }

        //concat all into output
        // FixedString64Bytes projectElement = "<Project Sdk=\"Microsoft.NET.Sdk\">\n";
        output.Append(projectElement);

        output.AppendFormat(propertyGroupFormatString, langVersion, unsafeCode, assemblySearchPaths);
        output.AppendFormat(definesFormatString, defines);

        //compile and reference belong in an itemgroup
        output.AppendFormat(itemGroupFormatString, compileItemsXml, referenceItems);

        //TODO: project references in their own itemgroup
        NativeText projectRefs = new(Allocator.Temp);
        for (int i = 0; i < projectReferences.Length; i++)
        {
            var projectRefName = projectReferences[i];
            projectRefName.AppendRawByte((byte)'\n');
            projectRefs.Append(projectRefName);
        }

        //there can be stuff without project references
        if (projectRefs.Length > 1)
        {
            output.Append(itemGroupElement);
            output.Append(projectRefs);
            output.Append(itemGroupEndElement);
        }

        output.Append(projectEndElement);
    }
}
