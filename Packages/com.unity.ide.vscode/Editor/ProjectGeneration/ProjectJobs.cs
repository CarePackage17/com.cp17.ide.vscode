using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using System.IO;
using System;
using Unity.Collections.LowLevel.Unsafe;

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
    [ReadOnly] public NativeList<UnsafeList<char>> utf16Files;
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
        NativeText compileItemsXml = new(Allocator.Temp);

        //write all cs files into <Compile /> items, like this:
        //<Compile Include="Packages/com.unity.ide.vscode/Editor/ProjectGeneration/ProjectGeneration.cs" />
        //<Compile Include="Packages/com.unity.ide.vscode/Editor/ProjectGeneration/FileIO.cs" />
        //...
        NativeText sourceFileTextUtf8 = new(4096, Allocator.Temp);
        for (int i = 0; i < utf16Files.Length; i++)
        {
            sourceFileTextUtf8.Clear();
            UnsafeList<char> filePathUtf16 = utf16Files[i];
            unsafe
            {
                byte* destPtr = sourceFileTextUtf8.GetUnsafePtr();
                CopyError err = UTF8ArrayUnsafeUtility.Copy(destPtr, out int destLength, sourceFileTextUtf8.Length, filePathUtf16.Ptr, filePathUtf16.Length);

                if (err == CopyError.Truncation)
                {
                    //Resize buffer and try again...once
                    if (sourceFileTextUtf8.TryResize(filePathUtf16.Length))
                    {
                        err = UTF8ArrayUnsafeUtility.Copy(destPtr, out destLength, sourceFileTextUtf8.Length, filePathUtf16.Ptr, filePathUtf16.Length);
                    }
                }
            }

            //Don't forget to dispose!
            filePathUtf16.Dispose();

            compileItemsXml.AppendFormat(compileFormatString, sourceFileTextUtf8);
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
