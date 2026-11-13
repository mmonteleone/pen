using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Serialization;
using Pen.Projects;

namespace Pen.Configuration;

internal enum ConfigKind { User, Project }
