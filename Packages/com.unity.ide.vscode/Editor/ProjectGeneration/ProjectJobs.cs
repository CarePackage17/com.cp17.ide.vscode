using Unity.Jobs;
using Unity.Collections;

struct GenerateProjectJob : IJob
{
    [ReadOnly] public FixedString4096Bytes assemblyName;
    [ReadOnly] public FixedString64Bytes langVersion;
    [ReadOnly] public bool unsafeCode;
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
        output.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
        output.AppendFormat(propertyGroupFormatString, langVersion, new FixedString32Bytes(unsafeCode.ToString()), assemblySearchPaths);
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
            output.Append("<ItemGroup>\n");
            output.Append(projectRefs);
            output.Append("</ItemGroup>\n");
        }

        output.Append("</Project>\n");
    }
}
