module Yession.Tests.SupervisorRehearsalMain

// The supervisor of a Seatbelt sandbox as a rehearsal runs it: the code of the production
// entry (`app/SandboxSupervisor.fs`), with SIGURG in the place of the signal that ends a
// process. A process ignores SIGURG unless it listens for it, so a pass of this program ends
// nothing, whatever confines it. A case can thus end its input, which is the stop, and see
// from a witness whether it made a pass.
//
// Fable compiles it to its own module and nothing in the suite imports it, so its top level
// runs only in a child started on it. The .NET CLR compiles none of it: there the suite is
// one program, and this top level would run at its start.

#if FABLE_COMPILER
Yession.Host.Supervisor.run "SIGURG"
#endif
