<!-- An absolute URL, so the logo shows on nuget.org too (it renders the README of the package). -->
<img src="https://raw.githubusercontent.com/vladislavtrayan/groundcontrol/main/assets/logo.svg" alt="GroundControl logo" width="96" height="96">

# GroundControl

Run your xUnit.net tests inside your Docker Compose project, from the IDE or `dotnet test`.

Your tests reach services by their Compose names (`http://api`, `postgres:5432`), and services can call your tests back. The runner, the results and the debugger stay on your machine, so nothing about your workflow changes.

## Getting started

You need .NET 8 or later, xUnit.net v3 and Docker.

1. Add the package to your test project and make GroundControl its test framework:

    ```xml
    <ItemGroup>
        <PackageReference Include="GroundControl" Version="0.3.2" />
    </ItemGroup>

    <ItemGroup>
        <AssemblyAttribute Include="Xunit.TestFrameworkAttribute">
            <_Parameter1>GroundControl.GroundControlTestFramework</_Parameter1>
            <_Parameter1_TypeName>System.Type</_Parameter1_TypeName>
        </AssemblyAttribute>
    </ItemGroup>
    ```

2. Add a `groundcontrol` service to your Compose file. Use an image with the .NET runtime your tests target, and mount the directory that holds your test assemblies:

    ```yaml
    services:
      groundcontrol:
        image: mcr.microsoft.com/dotnet/aspnet:10.0
        command: [ "sleep", "infinity" ]
        volumes:
          - .:/workspace:ro
    ```

3. Start the project:

    ```bash
    docker compose up --detach --build
    ```

4. Run your tests as you always do.

GroundControl looks for the closest `compose.yaml` above your test assembly to find the project.

## How it works

Your test assembly runs inside the `groundcontrol` service, which lives as long as your project does. GroundControl never creates, replaces or removes that container; it only runs tests in it and streams the results back to your runner.

Several test projects running at the same time share the container, just as they would share your machine.

Before a run, GroundControl checks that the service mounts your test assembly and has `sh` and the right .NET runtime, and tells you which image to use if it doesn't. It then waits for your services to become healthy and for one-off jobs such as migrations to finish. If the `groundcontrol` service itself is stopped, it asks you to start it.

## Calling your tests from services

Inside the project network your tests are reachable as `groundcontrol`. If a test fixture hosts a web server on port 8080, a service can call it at `http://groundcontrol:8080`.

Two things to keep in mind:

- Listen on all interfaces (`http://*:8080` or `0.0.0.0`), not on `localhost`.
- Read the name from the `GROUNDCONTROL_CONTAINER` environment variable instead of hard-coding it.

Test projects running at the same time must not listen on the same port, just like on your machine. Parallel pipelines have their own projects and don't interfere.

If the process that started a test run dies, its tests are stopped within about 30 seconds, so they don't keep ports busy.

## Settings

Everything is optional. Set a value as assembly metadata in the test project:

```xml
<ItemGroup>
    <AssemblyMetadata Include="GroundControl.Network" Value="backend" />
</ItemGroup>
```

Or as an environment variable, which wins: `GROUNDCONTROL_NETWORK=backend`.

| Setting | What it does | Default |
| --- | --- | --- |
| `Project` | Compose project name. With it, GroundControl doesn't need to find a Compose file. | found by the project directory |
| `ProjectDirectory` | Compose project directory, relative to the test assembly | closest `compose.yaml` |
| `Container` | The tests service, and the name services reach the tests by | `groundcontrol` |
| `Network` | The network the tests service has to be on | the one most services use |
| `ReadinessTimeout` | How long to wait for the project, in seconds or `hh:mm:ss`; `0` doesn't wait | 5 minutes |
| `DockerHost` | Docker Engine endpoint, e.g. `tcp://build:2375` | `DOCKER_HOST` |
| `DockerContext` | Docker CLI context to use | the current one |

## Debugging

Start debugging as usual. The test process in the container waits for the debugger to attach.

- **Rider** attaches on its own once you install the [GroundControl plugin](https://plugins.jetbrains.com/plugin/35827-groundcontrol) from the JetBrains Marketplace.
- **Other IDEs**: attach to the process by hand. The debug output shows the container and the process ID.

## Development

`dotnet test` runs the unit tests and, when Docker is available, the integration tests against the sample project in `test/GroundControl.Tests.Sample`. The sample declares its tests service in `compose.groundcontrol.yaml`.

`rider/build.ps1` builds the Rider plugin and installs it into your local Rider.

Pushing a version tag such as `0.3.2` publishes the package to nuget.org and creates a GitHub release with the plugin.

## License

[MIT](LICENSE)
