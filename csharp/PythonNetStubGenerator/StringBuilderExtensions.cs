using System;
using System.Collections.Generic;
using System.IO;

namespace PythonNetStubGenerator
{
    
    public static class StringBuilderExtensions
    {
        public static TextWriter Indent(this TextWriter tw)
        {
            for (var i = 0; i < IndentScope.IndentLevel; i++) tw.Write("    ");
            return tw;
        }

        public static string CommaJoin(this IEnumerable<string> strings) => string.Join(", ", strings);
    }

    public class IndentScope: IDisposable
    {
        public static int IndentLevel;
        public IndentScope() => IndentLevel++;
        public void Dispose() => IndentLevel--;
    }
    
}