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
    // public FixedString64Bytes guidFormatString;
}

struct ProjectReference
{
    public FixedString4096Bytes name;
    public FixedString64Bytes guid;

    public ProjectReference(FixedString4096Bytes name, FixedString64Bytes guid)
    {
        this.name = name;
        this.guid = guid;
    }
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
    //we could try to convert this into a parallelfor job by using unsafetext here
    [ReadOnly] public NativeText defines;
    [ReadOnly] public NativeText files;
    [ReadOnly] public NativeText assemblySearchPaths;
    [ReadOnly] public NativeArray<FixedString4096Bytes> assemblyReferences;
    [ReadOnly] public NativeArray<ProjectReference> projectReferences;
    [ReadOnly] public FixedString64Bytes compileFormatString;
    [ReadOnly] public FixedString64Bytes referenceFormatString;
    [ReadOnly] public FixedString64Bytes itemGroupFormatString;
    [ReadOnly] public FixedString4096Bytes propertyGroupFormatString;
    //excluded from project generation
    [ReadOnly] public NativeParallelHashSet<FixedString4096Bytes> excludedAssemblies;

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

        //project references in their own itemgroup
        NativeText projectRefs = new(Allocator.Temp);

        // <ProjectReference Include="Assembly-CSharp.csproj">
        //   <Project>{29b64283-c21a-f655-ab7b-f58eb1e6716a}</Project>
        //   <Name>Assembly-CSharp</Name>
        // </ProjectReference>
        for (int i = 0; i < projectReferences.Length; i++)
        {
            ProjectReference projectRef = projectReferences[i];

            //If the referenced project is excluded from generation we want a regular reference instead of projectreference
            if (excludedAssemblies.Contains(projectRef.name))
            {
                referenceItems.AppendFormat(referenceFormatString, projectRef.name);
                continue;
            }

            var projectRefName = projectRef.name;
            projectRefs.AppendFormat(projectReferenceStrings.projectReferenceStart, projectRefName);

            //TODO: generate guid for name
            //in unity's code this is done via MD5 class, but we can't use it with burst...
            //I wonder if xxhash is ok here?
            //according to random dude on SO, yes:
            //https://stackoverflow.com/a/45789658
            // Unity.Mathematics.uint4 hash = xxHash3.Hash128(projectRefName);
            FixedString64Bytes guidString = new();

            // //aw fuck, this does decimal formatting only, but we need hex...
            // //maybe we can copy-port out of here:
            // //https://github.com/Unity-Technologies/mono/blob/2021.3.19f1/mcs/class/referencesource/mscorlib/system/guid.cs#L1194
            // //or we move this to a non-bursted job where we use managed APIs to do the work for us
            // //and pass the results into this...
            // FixedString32Bytes a = new();
            // a.Append(hash.x);
            // FixedString32Bytes b = new();
            // b.Append(hash.y >> 16); //upper 16 bits
            // FixedString32Bytes c = new();
            // c.Append(hash.y & 0x00FF); //lower 16 bits
            // FixedString32Bytes d = new();
            // d.Append(hash.z >> 16);
            // FixedString32Bytes e = new();
            // e.Append(hash.z & 0x0FF);
            // e.Append(hash.w);

            // guidString.Add((byte)'{');
            // guidString.AppendFormat(projectReferenceStrings.guidFormatString, a, b, c, d, e);
            // guidString.Add((byte)'}');

            // projectRefs.AppendFormat(projectReferenceStrings.projectFormatString, guidString);

            guidString.Add((byte)'{');
            guidString.Append(projectRef.guid);
            guidString.Add((byte)'}');
            projectRefs.AppendFormat(projectReferenceStrings.projectFormatString, guidString);

            projectRefs.AppendFormat(projectReferenceStrings.nameFormatString, projectRefName);
            projectRefs.Append(projectReferenceStrings.projectReferenceEnd);
            projectRefs.Add((byte)'\n');
        }

        //concat all into output
        // FixedString64Bytes projectElement = "<Project Sdk=\"Microsoft.NET.Sdk\">\n";
        output.Append(projectElement);

        output.AppendFormat(propertyGroupFormatString, langVersion, unsafeCode, assemblySearchPaths, defines);

        //compile and reference belong in an itemgroup
        output.AppendFormat(itemGroupFormatString, compileItemsXml, referenceItems);



        //There can be projects without any project references. Don't write anything in that case.
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

// Maybe this is overthinking. Let's do the easy thing first and go from there.
// struct GenerateGuidJob : IJob
// {
//     [ReadOnly] public FixedString4096Bytes assemblyName;
//     [WriteOnly] public FixedString64Bytes output;

//     public void Execute()
//     {
//         Unity.Mathematics.uint4 hash = xxHash3.Hash128(assemblyName);
//         var span = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref hash, 16);
//         ReadOnlySpan<byte> guidBytes = MemoryMarshal.AsBytes(span);

//         Guid g = new(guidBytes);
//         string guidStr = g.ToString();

//         output = new(guidStr);
//     }
// }
