using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using System.IO;
using System;
using Unity.Collections.LowLevel.Unsafe;

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
    [ReadOnly] public bool unsafeCode;
    [ReadOnly] public NativeList<UnsafeList<char>> definesUtf16;
    [ReadOnly] public NativeList<UnsafeList<char>> utf16Files;
    [ReadOnly] public NativeText assemblySearchPaths;
    [ReadOnly] public NativeArray<FixedString4096Bytes> assemblyReferences;
    [ReadOnly] public NativeArray<ProjectReference> projectReferences;
    //excluded from project generation
    [ReadOnly] public NativeParallelHashSet<FixedString4096Bytes> excludedAssemblies;

    public NativeText projectXmlOutput;

    public void Execute()
    {
        NativeText compileItemsXml = new(Allocator.Temp);

        FixedString32Bytes compileFormatString = "<Compile Include=\"{0}\" />\n";

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

        //Preprocessor defines look like this in the project file:
        //DEBUG;TRACE;UNITY_2021;NO_THIS_IS_PATRICK
        NativeText defines = new(4096, Allocator.Temp);
        NativeText tmpUtf8 = new(512, Allocator.Temp);
        for (int i = 0; i < definesUtf16.Length; i++)
        {
            tmpUtf8.Clear();
            UnsafeList<char> defineUtf16 = definesUtf16[i];

            unsafe
            {
                byte* destPtr = tmpUtf8.GetUnsafePtr();
                CopyError err = UTF8ArrayUnsafeUtility.Copy(destPtr, out int destLength, tmpUtf8.Length, defineUtf16.Ptr, defineUtf16.Length);

                if (err == CopyError.Truncation && tmpUtf8.TryResize(defineUtf16.Length))
                {
                    err = UTF8ArrayUnsafeUtility.Copy(destPtr, out destLength, tmpUtf8.Length, defineUtf16.Ptr, defineUtf16.Length);
                }
            }

            defines.Append(tmpUtf8);
            defines.Add((byte)';');
            defineUtf16.Dispose();
        }

        //write all refs into <Include /> items
        NativeText referenceItems = new(Allocator.Temp);
        FixedString32Bytes referenceFormatString = "<Reference Include=\"{0}\" />\n";

        for (int i = 0; i < assemblyReferences.Length; i++)
        {
            referenceItems.AppendFormat(referenceFormatString, assemblyReferences[i]);
        }

        //Project references are in their own ItemGroup.
        // <ProjectReference Include="Assembly-CSharp.csproj">
        //   <Project>{29b64283-c21a-f655-ab7b-f58eb1e6716a}</Project>
        //   <Name>Assembly-CSharp</Name>
        // </ProjectReference>
        NativeText projectRefs = new(Allocator.Temp);

        FixedString64Bytes projectReferenceStart = "<ProjectReference Include=\"{0}.csproj\">";
        FixedString32Bytes projectReferenceEnd = "</ProjectReference>";
        FixedString32Bytes projectFormatString = "<Project>{0}</Project>";
        FixedString32Bytes nameFormatString = "<Name>{0}</Name>";

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
            projectRefs.AppendFormat(projectReferenceStart, projectRefName);

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
            projectRefs.AppendFormat(projectFormatString, guidString);

            projectRefs.AppendFormat(nameFormatString, projectRefName);
            projectRefs.Append(projectReferenceEnd);
            projectRefs.Add((byte)'\n');
        }

        //concat all into output
        FixedString64Bytes projectElement = "<Project Sdk=\"Microsoft.NET.Sdk\">\n";
        projectXmlOutput.Append(projectElement);

        FixedString4096Bytes propertyGroupFormatString = "<PropertyGroup>\n" +
                        "<TargetFramework>netstandard2.1</TargetFramework>\n" + //make this configurable as well
                        "<LangVersion>{0}</LangVersion>\n" +
                        "<EnableDefaultItems>false</EnableDefaultItems>\n" +
                        "<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>\n" +
                        "<GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n" +
                        "<Deterministic>true</Deterministic>\n" +
                        "<OutputPath>Temp</OutputPath>\n" +
                        "<AllowUnsafeBlocks>{1}</AllowUnsafeBlocks>\n" +
                        "<AssemblySearchPaths>{2}</AssemblySearchPaths>\n" +
                        "<DefineConstants>{3}</DefineConstants>\n" +
                    "</PropertyGroup>\n";
        FixedString32Bytes unsafeStr = unsafeCode ? "true" : "false";
        projectXmlOutput.AppendFormat(propertyGroupFormatString, langVersion, unsafeStr, assemblySearchPaths, defines);

        //compile and reference belong in an itemgroup
        FixedString64Bytes itemGroupFormatString = "<ItemGroup>\n{0}\n{1}\n</ItemGroup>\n";
        projectXmlOutput.AppendFormat(itemGroupFormatString, compileItemsXml, referenceItems);

        FixedString32Bytes itemGroupElement = "<ItemGroup>\n";
        FixedString32Bytes itemGroupEndElement = "</ItemGroup>\n";
        //There can be projects without any project references. Don't write anything in that case.
        if (projectReferences.Length > 0)
        {
            projectXmlOutput.Append(itemGroupElement);
            projectXmlOutput.Append(projectRefs);
            projectXmlOutput.Append(itemGroupEndElement);
        }

        FixedString32Bytes projectEndElement = "</Project>\n";
        projectXmlOutput.Append(projectEndElement);
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

[BurstCompile]
struct GenerateSlnJob : IJob
{
    //maybe rename projectreference to something that fits both cases
    [ReadOnly] public NativeList<ProjectReference> projectsInSln;

    public NativeText output;

    public void Execute()
    {
        FixedString512Bytes slnHeader = "Microsoft Visual Studio Solution File, Format Version 12.00";
        FixedString4096Bytes projectFormatString = "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") " +
            "= \"{0}\", \"{0}.csproj\", \"{1}\"\nEndProject\n";

        output.Append(slnHeader);
        output.Add((byte)'\n');

        for (int i = 0; i < projectsInSln.Length; i++)
        {
            var projData = projectsInSln[i];

            //There is a bug with nested braces and AppendFormat, so we do this manually.
            FixedString64Bytes guidWithBraces = new();
            guidWithBraces.Add((byte)'{');
            guidWithBraces.Append(projData.guid);
            guidWithBraces.Add((byte)'}');

            output.AppendFormat(projectFormatString, projData.name, guidWithBraces);
        }

        FixedString32Bytes global = "Global\n";
        output.Append(global);

        //indent with tabs
        output.Add((byte)'\t');
        FixedString64Bytes globalSectionPre = "GlobalSection(SolutionConfigurationPlatforms) = preSolution\n";
        output.Append(globalSectionPre);

        output.Add((byte)'\t');
        output.Add((byte)'\t');
        FixedString64Bytes debugAnyCpu = "Debug|Any CPU = Debug|Any CPU\n";
        output.Append(debugAnyCpu);

        output.Add((byte)'\t');
        FixedString32Bytes endGlobalSection = "EndGlobalSection\n";
        output.Append(endGlobalSection);

        output.Add((byte)'\t');
        FixedString64Bytes globalSectionPost = "GlobalSection(ProjectConfigurationPlatforms) = postSolution\n";
        output.Append(globalSectionPost);

        FixedString64Bytes cfgLineFormat = "{0}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\n";
        FixedString64Bytes buildLineFormat = "{0}.Debug|Any CPU.Build.0 = Debug|Any CPU\n";

        for (int i = 0; i < projectsInSln.Length; i++)
        {
            //There is a bug with nested braces and AppendFormat, so we do this manually (sigh).
            FixedString64Bytes guid = new();
            guid.Add((byte)'{');
            guid.Append(projectsInSln[i].guid);
            guid.Add((byte)'}');

            output.Add((byte)'\t');
            output.Add((byte)'\t');
            output.AppendFormat(cfgLineFormat, guid);

            output.Add((byte)'\t');
            output.Add((byte)'\t');
            output.AppendFormat(buildLineFormat, guid);
        }

        output.Add((byte)'\t');
        output.Append(endGlobalSection);

        //We need a block like this:
        //GlobalSection(SolutionConfigurationPlatforms) = preSolution
        // 	Debug|Any CPU = Debug|Any CPU
        //EndGlobalSection

        //and another one like this:
        //GlobalSection(ProjectConfigurationPlatforms) = postSolution
        // 	{98f2dd1b-074b-cf3b-5f2d-f208ae635e6d}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        // 	{98f2dd1b-074b-cf3b-5f2d-f208ae635e6d}.Debug|Any CPU.Build.0 = Debug|Any CPU
        // 	{6902571f-3a69-cc40-44fe-2cf06c5cba36}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        // 	{6902571f-3a69-cc40-44fe-2cf06c5cba36}.Debug|Any CPU.Build.0 = Debug|Any CPU
        // 	{0af454fe-e3d5-234b-8e33-50684044af23}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        // 	{0af454fe-e3d5-234b-8e33-50684044af23}.Debug|Any CPU.Build.0 = Debug|Any CPU
        // ...
        // EndGlobalSection

        FixedString32Bytes endGlobal = "EndGlobal\n";
        output.Append(endGlobal);
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
