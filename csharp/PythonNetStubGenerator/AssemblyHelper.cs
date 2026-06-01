using System.Reflection;
using System.Runtime.Loader;

namespace PythonNetStubGenerator
{
    internal class AssemblyHelper
    {
        private static readonly AssemblyLoadContext TargetLibContext =
            new AssemblyLoadContext("TargetLibContext");

        /// <summary>
        /// Loads the contents of an assembly file on the specified path.
        /// Assembly is loaded in a context, separate from the stub generator
        /// application.
        /// </summary>
        /// <param name="assemblyPath">The fully qualified path of the file to
        /// load.</param>
        /// <returns>The loaded assembly.</returns>
        public static Assembly LoadFromPath(string assemblyPath)
        {
            return TargetLibContext.LoadFromAssemblyPath(assemblyPath);
        }
    }
}
