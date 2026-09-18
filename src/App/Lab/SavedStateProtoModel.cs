using ProtoBuf;
using ProtoBuf.Meta;

namespace DotNetLab.Lab;

[ProtoModel]
[ProtoSerializable(typeof(SavedState))]
internal partial class SavedStateProtoModel : TypeModel
{
}
