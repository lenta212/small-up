using Robust.Shared.Serialization;

namespace Content.Shared.PDA.Ringer
{
    [Serializable, NetSerializable]
    public sealed class RingerUpdateState : BoundUserInterfaceState
    {
        public bool IsPlaying;
        public Note[] Ringtone;
        public bool PreserveEditorInput;
        public bool IsPreview;

        public RingerUpdateState(
            bool isPlay,
            Note[] ringtone,
            bool preserveEditorInput = false,
            bool isPreview = false)
        {
            IsPlaying = isPlay;
            Ringtone = ringtone;
            PreserveEditorInput = preserveEditorInput;
            IsPreview = isPreview;
        }
    }

}
