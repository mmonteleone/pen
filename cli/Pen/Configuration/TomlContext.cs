using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Serialization;
using Pen.Platform;

namespace Pen.Configuration;

/// <summary>Source-generated TOML metadata: the untyped model only, so no reflection (Native AOT).</summary>
[TomlSerializable(typeof(TomlTable))]
internal sealed partial class TomlContext : TomlSerializerContext;
