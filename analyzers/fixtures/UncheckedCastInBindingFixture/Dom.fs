namespace Browser.Types

// The slice of Fable.Browser.Dom's shapes the event cases need, declared under the names the
// rule matches (`Browser.Types.Event`, `Browser.Types.EventTarget`) rather than referenced, so
// the fixture restores nothing it does not otherwise need.

type EventTarget =
    abstract dispatchEvent: unit -> bool

type Event =
    abstract target: EventTarget
    abstract currentTarget: EventTarget

type KeyboardEvent =
    inherit Event
    abstract key: string

type Node =
    inherit EventTarget
    abstract nodeName: string

type Element =
    inherit Node
    abstract tagName: string

type HTMLElement =
    inherit Element
    abstract focus: unit -> unit
