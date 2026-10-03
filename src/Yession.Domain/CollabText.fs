namespace Yession.Domain

/// Collaborative text, as the model needs it: what a session title or a chapter name IS,
/// without saying how two people's edits to one merge.
///
/// A hole each component plugs with its own CRDT text. The App and the Session both plug
/// `Ylmish.Text`, whose splices are what let two people renaming one chapter interleave per
/// character rather than clobber; the domain asks only the three things a model needs of
/// it, so it can say what a name is without depending on how one syncs.
type CollabText<'Text> =
    { /// Nothing written.
      Empty : 'Text
      /// Words, as text nobody has edited yet.
      OfString : string -> 'Text
      /// What the text currently says.
      ToString : 'Text -> string }
