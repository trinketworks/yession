namespace A2ui

// The A2UI v0.9.1 protocol and its basic catalog, as types.
//
// The shapes follow `specification/v0_9_1/json/*.json` and
// `specification/v0_9_1/catalogs/basic/catalog.json` one for one. An optional property the
// spec gives a default is still an `option` here, never the default filled in: a surface that
// is decoded and encoded again says what its author said, and the default is the renderer's
// business.

/// A JSON value, for the places the spec leaves open: the data model, an action's context, a
/// literal array. Portable on purpose: Thoth's `JsonValue` is a different type on each runtime
/// and compares by reference under Fable.
type Json =
    | JNull
    | JBool of bool
    | JNumber of float
    | JString of string
    | JArray of Json list
    | JObject of (string * Json) list

/// The `returnType` a function call can state.
[<RequireQualifiedAccess>]
type ReturnType =
    | String
    | Number
    | Boolean
    | Array
    | Object
    | Any
    | Void

/// A value that is a literal, a JSON pointer into the data model (`{"path": ...}`), or a
/// function call. A pointer without a leading `/` is relative to a template's item.
type Dynamic<'a> =
    | Literal of 'a
    | Bound of path: string
    | Call of FunctionCall

/// A call into the catalog, with the `returnType` its author stated, if any.
and FunctionCall =
    { Function : Function
      ReturnType : ReturnType option }

/// The basic catalog's functions, with their arguments.
and Function =
    | Required of value: Dynamic<Json>
    | Regex of value: Dynamic<string> * pattern: string
    | Length of value: Dynamic<string> * min: int option * max: int option
    | Numeric of value: Dynamic<float> * min: float option * max: float option
    | Email of value: Dynamic<string>
    | FormatString of value: Dynamic<string>
    | FormatNumber of value: Dynamic<float> * decimals: Dynamic<float> option * grouping: Dynamic<bool> option
    | FormatCurrency of
        value: Dynamic<float> *
        currency: Dynamic<string> *
        decimals: Dynamic<float> option *
        grouping: Dynamic<bool> option
    | FormatDate of value: Dynamic<Json> * format: Dynamic<string>
    | Pluralize of value: Dynamic<float> * forms: PluralForms
    | OpenUrl of url: string
    | And of Dynamic<bool> list
    | Or of Dynamic<bool> list
    | Not of Dynamic<bool>

and PluralForms =
    { Zero : Dynamic<string> option
      One : Dynamic<string> option
      Two : Dynamic<string> option
      Few : Dynamic<string> option
      Many : Dynamic<string> option
      Other : Dynamic<string> }

type Accessibility =
    { Label : Dynamic<string> option
      Description : Dynamic<string> option }

/// A validation rule on an input or a button: the surface is valid where `Condition` is true.
type CheckRule =
    { Condition : Dynamic<bool>
      Message : string }

/// A container's children: listed, or one template stamped out per item of a data model list.
type ChildList =
    | Static of string list
    | Template of componentId: string * path: string

/// What pressing a button does: tell the server (`event`), or call a catalog function locally.
[<RequireQualifiedAccess>]
type Action =
    | Event of name: string * context: (string * Dynamic<Json>) list
    | LocalCall of FunctionCall

[<RequireQualifiedAccess>]
type TextVariant = H1 | H2 | H3 | H4 | H5 | Caption | Body

[<RequireQualifiedAccess>]
type ImageFit = Contain | Cover | Fill | NoScale | ScaleDown

[<RequireQualifiedAccess>]
type ImageVariant = Icon | Avatar | SmallFeature | MediumFeature | LargeFeature | Header

/// An icon: one of the catalog's names, an SVG path, or a pointer to either.
[<RequireQualifiedAccess>]
type IconSource =
    | Named of string
    | Svg of path: string
    | Bound of path: string

[<RequireQualifiedAccess>]
type Justify = Start | Center | End | SpaceBetween | SpaceAround | SpaceEvenly | Stretch

[<RequireQualifiedAccess>]
type Align = Start | Center | End | Stretch

[<RequireQualifiedAccess>]
type Direction = Vertical | Horizontal

[<RequireQualifiedAccess>]
type ButtonVariant = Default | Primary | Borderless

[<RequireQualifiedAccess>]
type TextFieldVariant = LongText | Number | ShortText | Obscured

[<RequireQualifiedAccess>]
type ChoiceVariant = MultipleSelection | MutuallyExclusive

[<RequireQualifiedAccess>]
type ChoiceDisplay = Checkbox | Chips

type ChoiceOption =
    { Label : Dynamic<string>
      Value : string }

type Tab =
    { Title : Dynamic<string>
      Child : string }

/// One component of the basic catalog, with its own properties. The properties every
/// component has are on `Component`.
[<RequireQualifiedAccess>]
type ComponentKind =
    | Text of text: Dynamic<string> * variant: TextVariant option
    | Image of
        url: Dynamic<string> *
        description: Dynamic<string> option *
        fit: ImageFit option *
        variant: ImageVariant option
    | Icon of IconSource
    | Video of url: Dynamic<string>
    | AudioPlayer of url: Dynamic<string> * description: Dynamic<string> option
    | Row of children: ChildList * justify: Justify option * align: Align option
    | Column of children: ChildList * justify: Justify option * align: Align option
    | List of children: ChildList * direction: Direction option * align: Align option
    | Card of child: string
    | Tabs of Tab list
    | Modal of trigger: string * content: string
    | Divider of axis: Direction option
    | Button of child: string * variant: ButtonVariant option * action: Action * checks: CheckRule list
    | TextField of
        label: Dynamic<string> *
        value: Dynamic<string> option *
        variant: TextFieldVariant option *
        validationRegexp: string option *
        checks: CheckRule list
    | CheckBox of label: Dynamic<string> * value: Dynamic<bool> * checks: CheckRule list
    | ChoicePicker of
        label: Dynamic<string> option *
        variant: ChoiceVariant option *
        options: ChoiceOption list *
        value: Dynamic<string list> *
        displayStyle: ChoiceDisplay option *
        filterable: bool option *
        checks: CheckRule list
    | Slider of
        label: Dynamic<string> option *
        min: float option *
        max: float *
        value: Dynamic<float> *
        checks: CheckRule list
    | DateTimeInput of
        value: Dynamic<string> *
        enableDate: bool option *
        enableTime: bool option *
        min: Dynamic<string> option *
        max: Dynamic<string> option *
        label: Dynamic<string> option *
        checks: CheckRule list

type Component =
    { Id : string
      Accessibility : Accessibility option
      /// The `flex-grow` of a direct child of a Row or Column.
      Weight : float option
      Kind : ComponentKind }

type Theme =
    { PrimaryColor : string option
      IconUrl : string option
      AgentDisplayName : string option
      /// The theme schema admits properties it does not name. They are kept, not judged.
      Other : (string * Json) list }

/// What a server (the agent's side) tells a client about a surface.
type ServerToClient =
    | CreateSurface of surfaceId: string * catalogId: string * theme: Theme option * sendDataModel: bool option
    | UpdateComponents of surfaceId: string * components: Component list
    /// `Path` `None` and `Some "/"` both name the whole model. `Value` `None` removes the key.
    | UpdateDataModel of surfaceId: string * path: string option * value: Json option
    | DeleteSurface of surfaceId: string

type ActionEvent =
    { Name : string
      SurfaceId : string
      SourceComponentId : string
      Timestamp : System.DateTimeOffset
      Context : (string * Json) list }

type ClientError =
    | ValidationFailed of surfaceId: string * path: string * message: string
    | Generic of code: string * surfaceId: string * message: string * other: (string * Json) list

/// What a client tells the server.
type ClientToServer =
    | ActionTaken of ActionEvent
    | ErrorReported of ClientError
