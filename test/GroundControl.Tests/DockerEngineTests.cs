using System.Security.Cryptography;
using System.Text;
using GroundControl.Docker;
using Xunit;

namespace GroundControl.Tests;

public class DockerEngineTests
{
    [Theory]
    [InlineData(true, "npipe://./pipe/docker_engine")]
    [InlineData(false, "unix:///var/run/docker.sock")]
    public void ResolvesDefaultContext(bool windows, string endpoint)
    {
        using var store = new ContextStore();

        Assert.Equal(endpoint, DockerEngine.ResolveEndpoint(null, "default", store.Directory, windows));
        Assert.Equal(endpoint, DockerEngine.ResolveEndpoint(null, null, store.Directory, windows));
    }

    [Fact]
    public void PrefersHost()
    {
        using var store = new ContextStore();

        Assert.Equal("tcp://build:2375", DockerEngine.ResolveEndpoint("tcp://build:2375", "desktop-linux", store.Directory, windows: true));
    }

    // The context store of the Docker CLI: the endpoints of the contexts by the hash of their names, and the current one in the configuration.
    [Fact]
    public void ResolvesContextOfStore()
    {
        using var store = new ContextStore();
        store.Add("desktop-linux", "npipe:////./pipe/dockerDesktopLinuxEngine");
        store.Add("remote", "tcp://build:2376");
        store.Current("desktop-linux");

        Assert.Equal("npipe://./pipe/dockerDesktopLinuxEngine", DockerEngine.ResolveEndpoint(null, null, store.Directory, windows: true));
        Assert.Equal("tcp://build:2376", DockerEngine.ResolveEndpoint(null, "remote", store.Directory, windows: true));
        Assert.Contains("missing", Assert.Throws<InvalidOperationException>(() => DockerEngine.ResolveEndpoint(null, "missing", store.Directory, windows: true)).Message);
    }

    private sealed class ContextStore : IDisposable
    {
        private readonly DirectoryInfo directory = System.IO.Directory.CreateTempSubdirectory("groundcontrol-docker-");

        public string Directory => this.directory.FullName;

        public void Add(string name, string endpoint)
        {
            var meta = this.directory.CreateSubdirectory(Path.Combine("contexts", "meta", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()));
            File.WriteAllText(
                Path.Combine(meta.FullName, "meta.json"),
                """{"Name":"NAME","Metadata":{},"Endpoints":{"docker":{"Host":"ENDPOINT","SkipTLSVerify":false}}}""".Replace("NAME", name).Replace("ENDPOINT", endpoint));
        }

        public void Current(string name) => File.WriteAllText(Path.Combine(this.Directory, "config.json"), """{"auths":{},"currentContext":"NAME"}""".Replace("NAME", name));

        public void Dispose() => this.directory.Delete(recursive: true);
    }
}
