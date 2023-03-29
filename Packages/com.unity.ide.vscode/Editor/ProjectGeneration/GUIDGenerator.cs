namespace VSCodeEditor
{
    public interface IGUIDGenerator
    {
        string ProjectGuid(string projectName, string assemblyName);
        string SolutionGuid { get; }
    }

    class GUIDProvider : IGUIDGenerator
    {
        public string ProjectGuid(string projectName, string assemblyName)
        {
            return SolutionGuidGenerator.GuidForProject(projectName + assemblyName);
        }

        public string SolutionGuid => "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
    }
}
