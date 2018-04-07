using System.Runtime.CompilerServices;

//When building in Unity, these are the names of the assemblies
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor-firstpass")]

//When building in visual studio, these are the names of the assemblies
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor.dll")]
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor-firstpass.dll")]