module Yession.Tests.VolumeLeases

// A sandbox's lease on a shared volume, kept current from inside it (`app/VolumeLeases.fs`):
// pinned when the sandbox comes up, every interval while it lives, once more before it goes.
// Driven with a scripted sandbox and a clock turned by hand, so every renewal happens when a
// case says so and nothing here waits on real time.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Host
open Yession.Tests.Support

let private started = DateTimeOffset (2026, 9, 30, 0, 0, 0, TimeSpan.Zero)

let private nix : VolumeMaintenance = { Pin = "/nix/var/yession/pin"; Every = TimeSpan.FromMinutes 1.0 }
// Shorter than a pin's limit (`VolumeLeases.pinLimit`), so a renewal can come due while a
// pin is still inside it.

let private maintained = Map.ofList [ "yession-nix", nix ]

/// A process's end, settled once by whoever gets there first.
type private Ending () =
    let mutable value : SandboxRun option = None
    let mutable waiting : (SandboxRun -> unit) list = []
    member _.Settle (run: SandboxRun) =
        if value.IsNone then
            value <- Some run
            let woken = waiting
            waiting <- []
            woken |> List.iter (fun wake -> wake run)
    member _.Wait : Async<SandboxRun> =
        Async.FromContinuations (fun (resolve, _, _) ->
            match value with
            | Some run -> resolve run
            | None -> waiting <- resolve :: waiting)

/// A sandbox whose processes end when the case says, recording what it was asked to run,
/// what it killed, and whether it was disposed.
type private Scripted () =
    let running = ResizeArray<Ending> ()
    member val Ran = ResizeArray<SandboxExec> ()
    member val Killed = 0 with get, set
    member val Disposed = false with get, set
    /// End the oldest running process with this code.
    member _.Finish (code: int) =
        let ending = running.[0]
        running.RemoveAt 0
        ending.Settle (SandboxExited code)
    member _.Running = running.Count
    member this.Sandbox : Sandbox =
        { Ref = "scripted"
          Spawn =
            fun exec _ ->
                async {
                    this.Ran.Add exec
                    let ending = Ending ()
                    running.Add ending
                    return
                        Ok
                            { WriteStdin = ignore
                              CloseStdin = ignore
                              Kill =
                                fun () ->
                                    this.Killed <- this.Killed + 1
                                    running.Remove ending |> ignore
                                    ending.Settle (SandboxExited 137)
                              Exited = ending.Wait }
                }
          SpawnPty = None
          Shell = None
          Dispose = fun () -> async { this.Disposed <- true } }

let private holding (volumes: (string * string) list) = { emptyPolicy with Volumes = volumes }

/// `scripted`, created through the lease decoration under `policy`.
let private leased (clock: VirtualClock) (said: ResizeArray<string>) (scripted: Scripted) (policy: SandboxPolicy) =
    async {
        let create : CreateSandbox = fun _ -> async { return Ok scripted.Sandbox }
        match! VolumeLeases.around clock.Clock said.Add maintained "s1-dev" create policy with
        | Ok sandbox -> return sandbox
        | Error e -> return failwithf "the sandbox did not come up: %s" e
    }

let private leaseTests =
    testList "which volumes are leased, and where" [
        testCase "a lease is its sandbox's own directory under the volume's .yession" <| fun () ->
            Expect.equal
                (VolumeLeases.leaseDir "/nix/" "s1-dev")
                "/nix/.yession/leases/s1-dev"
                "one directory per sandbox, under the mount point, however it was spelled"

        testCase "only a maintained volume is leased" <| fun () ->
            let leases = VolumeLeases.leasesFor maintained "s1-dev" [ "yession-nix", "/nix"; "scratch", "/scratch" ]
            Expect.equal
                (leases |> List.map (fun lease -> lease.Volume, lease.Dir))
                [ "yession-nix", "/nix/.yession/leases/s1-dev" ]
                "a volume the operator did not ask to have maintained has nothing run for it"
    ]

let private lifeTests =
    testList "a lease kept for the sandbox's life" [
        testCaseAsync "a sandbox holding no maintained volume runs nothing" <| async {
            let clock = virtualClock started
            let scripted = Scripted ()
            let! sandbox = leased clock (ResizeArray ()) scripted (holding [ "scratch", "/scratch" ])
            do! sandbox.Dispose ()
            Expect.isEmpty scripted.Ran "nothing is run in a sandbox the operator asked nothing of"
        }

        testCaseAsync "the pin runs when the sandbox comes up, bare, with its lease as its one argument" <| async {
            let clock = virtualClock started
            let scripted = Scripted ()
            let! _ = leased clock (ResizeArray ()) scripted (holding [ "yession-nix", "/nix" ])
            Expect.equal
                (scripted.Ran |> Seq.map (fun exec -> exec.Executable, exec.Arguments, exec.Via) |> List.ofSeq)
                [ "/nix/var/yession/pin", [ "/nix/.yession/leases/s1-dev" ], Direct ]
                "the operator's pin, behind no entrypoint"
        }

        testCaseAsync "the lease is renewed every interval" <| async {
            let clock = virtualClock started
            let scripted = Scripted ()
            let! _ = leased clock (ResizeArray ()) scripted (holding [ "yession-nix", "/nix" ])
            scripted.Finish 0
            clock.Advance nix.Every
            Expect.equal scripted.Ran.Count 2 "once at start, once an interval later"
        }

        testCaseAsync "a renewal due while the last pin still runs is skipped, not queued" <| async {
            let clock = virtualClock started
            let scripted = Scripted ()
            let! _ = leased clock (ResizeArray ()) scripted (holding [ "yession-nix", "/nix" ])
            clock.Advance nix.Every
            Expect.equal scripted.Ran.Count 1 "two pins of one lease never run at once"
        }

        testCaseAsync "removing the sandbox pins once more first, and renews nothing after" <| async {
            let clock = virtualClock started
            let scripted = Scripted ()
            let! sandbox = leased clock (ResizeArray ()) scripted (holding [ "yession-nix", "/nix" ])
            scripted.Finish 0
            Async.StartImmediate (sandbox.Dispose ())
            Expect.equal scripted.Ran.Count 2 "the last word, before the sandbox goes"
            Expect.isFalse scripted.Disposed "and the sandbox waits for it"
            scripted.Finish 0
            Expect.isTrue scripted.Disposed "then goes"
            clock.Advance nix.Every
            Expect.equal scripted.Ran.Count 2 "and nothing is pinned after"
        }

        testCaseAsync "a pin that fails is said, and the sandbox carries on" <| async {
            let clock = virtualClock started
            let said = ResizeArray ()
            let scripted = Scripted ()
            let! _ = leased clock said scripted (holding [ "yession-nix", "/nix" ])
            scripted.Finish 3
            Expect.equal (List.ofSeq said) [ "volume yession-nix: pin /nix/var/yession/pin exited 3" ] "which volume, which pin, how"
            clock.Advance nix.Every
            Expect.equal scripted.Ran.Count 2 "and the next renewal still comes"
        }

        testCaseAsync "a pin still running at its limit is killed, so it holds nothing back" <| async {
            let clock = virtualClock started
            let said = ResizeArray ()
            let scripted = Scripted ()
            let! _ = leased clock said scripted (holding [ "yession-nix", "/nix" ])
            clock.Advance VolumeLeases.pinLimit
            Expect.equal scripted.Killed 1 "killed at its limit"
            clock.Advance nix.Every
            Expect.equal scripted.Running 1 "and the next renewal runs"
        }
    ]

let tests = testList "Volume leases" [ leaseTests; lifeTests ]
