using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using System.IO;
using System;

struct ProjectReferenceStrings
{
    public FixedString64Bytes projectReferenceStart;
    public FixedString32Bytes projectReferenceEnd;
    public FixedString32Bytes projectFormatString;
    public FixedString32Bytes nameFormatString;
}

//DisposeSentinel takes some time in editor and we don't want users to fiddle with their settings to get better
//perf. For debugging we can still force on, but disabling safety checks via attribute should shave off
//some time here.
[BurstCompile(/*DisableSafetyChecks = true*/)]
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
    [ReadOnly] public ProjectReferenceStrings projectReferenceStrings;

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

        //project references in their own itemgroup
        NativeText projectRefs = new(Allocator.Temp);

        // <ProjectReference Include="Assembly-CSharp.csproj">
        //   <Project>{29b64283-c21a-f655-ab7b-f58eb1e6716a}</Project>
        //   <Name>Assembly-CSharp</Name>
        // </ProjectReference>
        for (int i = 0; i < projectReferences.Length; i++)
        {
            FixedString4096Bytes projectRefName = projectReferences[i];
            projectRefs.AppendFormat(projectReferenceStrings.projectReferenceStart, projectRefName);

            //TODO: generate guid for name
            // Guid g = new Guid();
            FixedString64Bytes mockGuid = new(new Unicode.Rune(0xFFFD), 12);
            projectRefs.AppendFormat(projectReferenceStrings.projectFormatString, mockGuid);

            projectRefs.AppendFormat(projectReferenceStrings.nameFormatString, projectRefName);
            projectRefs.Append(projectReferenceStrings.projectReferenceEnd);
            projectRefs.Add((byte)'\n');
        }

        //there can be projects without any project references
        if (projectReferences.Length > 0)
        {
            output.Append(itemGroupElement);
            output.Append(projectRefs);
            output.Append(itemGroupEndElement);
        }

        output.Append(projectEndElement);
    }
}

struct WriteToFileJob : IJob
{
    [ReadOnly] public NativeText content;
    [ReadOnly] public FixedString4096Bytes filePath; //change this to nativetext to handle longer paths

    public void Execute()
    {
        using (FileStream fs = File.Open(filePath.ConvertToString(), FileMode.Create, FileAccess.Write))
        {
            ReadOnlySpan<byte> data;
            unsafe
            {
                data = new(content.GetUnsafePtr(), content.Length);
            }

            fs.Write(data);
        }
    }
}
