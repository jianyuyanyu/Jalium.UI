using System.Runtime.CompilerServices;

// The lowering is worth testing directly: it is the only place a @virtualize header is
// interpreted, and a mistake there stays invisible until run time.
[assembly: InternalsVisibleTo("Jalium.UI.Tests")]
