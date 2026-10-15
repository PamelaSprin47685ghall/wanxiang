namespace Wanxiang.Tests

open System
open System.Diagnostics
open System.IO
open Wanxiang.UI
open Xunit

module LocalStateTests =

    let private temporary action =
        let root = Path.Combine(Path.GetTempPath(), "wanxiang-state-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try action root
        finally Directory.Delete(root, true)

    let private git directory arguments =
        let start = ProcessStartInfo("git")
        start.UseShellExecute <- false
        start.WorkingDirectory <- directory
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        for argument in arguments do start.ArgumentList.Add argument
        use command = new Process(StartInfo = start)
        Assert.True(command.Start())
        let output = command.StandardOutput.ReadToEndAsync()
        let errors = command.StandardError.ReadToEndAsync()
        command.WaitForExit()
        Assert.True(command.ExitCode = 0, errors.GetAwaiter().GetResult())
        output.GetAwaiter().GetResult().Trim()

    let private repository directory =
        Directory.CreateDirectory directory |> ignore
        git directory [ "init"; "-q"; "-b"; "main" ] |> ignore
        File.WriteAllText(Path.Combine(directory, "source.txt"), "source")
        git directory [ "add"; "source.txt" ] |> ignore
        git directory [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid";
                        "commit"; "-qm"; "fixture" ] |> ignore

    [<Fact>]
    let ``main worktree subdirectory and linked worktree share one local state root`` () =
        temporary (fun root ->
            let main = Path.Combine(root, "main with spaces")
            let linked = Path.Combine(root, "linked")
            repository main
            git main [ "worktree"; "add"; "--detach"; linked ] |> ignore
            let nested = Path.Combine(main, "src", "nested")
            Directory.CreateDirectory nested |> ignore
            let expected = Path.Combine(main, ".Herdchestra")
            Assert.Equal(expected, LocalState.rootFor main)
            Assert.Equal(expected, LocalState.rootFor nested)
            Assert.Equal(expected, LocalState.rootFor linked)
            Assert.False(Directory.Exists expected))

    [<Fact>]
    let ``independent source checkout and non Git workspace do not share state`` () =
        temporary (fun root ->
            let app = Path.Combine(root, "app")
            let library = Path.Combine(app, "wanxiang2")
            repository app
            repository library
            let plain = Path.Combine(root, "plain")
            Directory.CreateDirectory plain |> ignore
            Assert.Equal(Path.Combine(app, ".Herdchestra"), LocalState.rootFor app)
            Assert.Equal(Path.Combine(library, ".Herdchestra"), LocalState.rootFor library)
            Assert.Equal(Path.Combine(plain, ".Herdchestra"), LocalState.rootFor plain))

    [<Fact>]
    let ``initialization changes no Git configuration and never imports old state`` () =
        temporary (fun root ->
            repository root
            let config = Path.Combine(root, ".git", "config")
            let exclude = Path.Combine(root, ".git", "info", "exclude")
            let beforeConfig = File.ReadAllBytes config
            let beforeExclude = File.ReadAllBytes exclude
            // 隔离夹具中的退役目录只用于反例，不是运行时恢复来源。
            let old = Path.Combine(root, ".git", "wanxiangshu", "events")
            Directory.CreateDirectory old |> ignore
            File.WriteAllText(Path.Combine(old, "invalid.ndjson"), "not a valid record")
            let state = LocalState.initialize root
            Assert.Equal("*\n", File.ReadAllText(Path.Combine(state, ".gitignore")))
            Assert.False(Directory.Exists(Path.Combine(state, "events")))
            Assert.Equal<byte>(beforeConfig, File.ReadAllBytes config)
            Assert.Equal<byte>(beforeExclude, File.ReadAllBytes exclude)
            Assert.Equal(state, LocalState.initialize root))

    [<Fact>]
    let ``Git paths and symlink aliases are refused before data access`` () =
        temporary (fun root ->
            repository root
            let gitDirectory = Path.Combine(root, ".git")
            Assert.Throws<ArgumentException>(fun () -> LocalState.rootFor gitDirectory |> ignore) |> ignore
            let alias = Path.Combine(root, "alias")
            Directory.CreateSymbolicLink(alias, gitDirectory) |> ignore
            Assert.Throws<ArgumentException>(fun () ->
                LocalState.requireOutsideGit(Path.Combine(alias, "state.sqlite")) |> ignore) |> ignore
            Directory.CreateSymbolicLink(Path.Combine(root, ".Herdchestra"), gitDirectory) |> ignore
            Assert.Throws<ArgumentException>(fun () -> LocalState.initialize root |> ignore) |> ignore)

    [<Fact>]
    let ``bare repositories and redirected ignore files are refused`` () =
        temporary (fun root ->
            let bare = Path.Combine(root, "bare")
            git root [ "init"; "--bare"; "-q"; bare ] |> ignore
            Assert.Throws<ArgumentException>(fun () -> LocalState.rootFor bare |> ignore) |> ignore
            let app = Path.Combine(root, "app")
            repository app
            let state = Path.Combine(app, ".Herdchestra")
            Directory.CreateDirectory state |> ignore
            let exclude = Path.Combine(app, ".git", "info", "exclude")
            let before = File.ReadAllBytes exclude
            File.CreateSymbolicLink(Path.Combine(state, ".gitignore"), exclude) |> ignore
            Assert.Throws<InvalidOperationException>(fun () -> LocalState.initialize app |> ignore) |> ignore
            Assert.Equal<byte>(before, File.ReadAllBytes exclude))
