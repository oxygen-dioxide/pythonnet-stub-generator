using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PythonNetStubGenerator
{
    public class StubWriter
    {
        private string _newLine = Environment.NewLine;

        public string NewLine {
            get
            {
                return _newLine;
            }
            set
            {
                if (value == null)
                {
                    value = Environment.NewLine;
                }
                _newLine = value;
            }
        }

        public string GetStub(string nameSpace, IEnumerable<Type> stubTypes)
        {
            var types = stubTypes as Type[] ?? stubTypes.ToArray();
            PythonTypes.CacheOverloadedNonGenericTypes(types);
            var typeGroups = types
                .Where(it => it.IsVisible) // Avoid internal classes
                .Where(it => it.DeclaringType == null) // Avoid Nested classes, they're handled later
                .GroupBy(it => it.NonGenericName())
                .OrderBy(it => it.Key).ToList();

            string body;
            using (var sw = new StringWriter { NewLine = NewLine })
            {
                var reservedSymbols = typeGroups.Select(it => it.Key);

                using (new SymbolScope(reservedSymbols, nameSpace))
                {
                    foreach (var typeGroup in typeGroups)
                        WriteTypeGroup(sw, typeGroup.Key, typeGroup);

                }
                body = sw.ToString();
            }

            //Prepend dependencies
            var deps = GetDependencies(nameSpace, body);
            return deps + body;
        }


        private static void WriteTypeGroup(TextWriter tw, string typeName, IEnumerable<Type> typeList)
        {
            var types = typeList.ToList();

            if (types.Contains(typeof(Array)))
            {
                WriteArrayType(tw, types.First());
                return;
            }

            if (types.Count == 1 && !types.Any(it => it.IsGenericTypeDefinition))
            {
                WriteType(tw, types.First());
                return;
            }

            var genericMetaclass = $"{typeName}_GenericClasses";
            var currentGenerics = ClassScope.AccessibleGenerics.Select(it => it.ToPythonType()).CommaJoin();
            if (!string.IsNullOrEmpty(currentGenerics)) currentGenerics = $"[{currentGenerics}]";


            var genericTypes = types.Where(it => it.IsGenericTypeDefinition).ToList();
            if (genericTypes.Any()) WriteTypeOverload(tw, genericMetaclass, genericTypes);

            var nonGeneric = types.FirstOrDefault(it => !it.IsGenericTypeDefinition);

            if (nonGeneric != null)
            {
                types.Remove(nonGeneric);
                var nonGenericName = $"{typeName}_0";
                tw.Indent().WriteLine($"class {typeName}({nonGenericName}, metaclass ={genericMetaclass}{currentGenerics}): ...");
                WriteType(tw, nonGeneric, nonGenericName);
            }
            else
            {
                tw.Indent().WriteLine($"{typeName} : {genericMetaclass}{currentGenerics}");
            }

            foreach (var type in types)
                WriteType(tw, type);
        }

        private static void WriteArrayType(TextWriter tw, Type arrayType)
        {
            var genericMetaclass = "Array_GenericClasses";

            tw.WriteLine();
            tw.Indent().WriteLine($"class {genericMetaclass}(abc.ABCMeta):");
            using (new IndentScope())
            {
                tw.Indent().WriteLine("Generic_Array_1_T = typing.TypeVar('Generic_Array_1_T')");
                tw.Indent().WriteLine("def __getitem__(self, types : typing.Type[Generic_Array_1_T]) -> typing.Type[Array_1[Generic_Array_1_T]]: ...");
            }

            tw.WriteLine();

            var nonGenericName = "Array_0";
            tw.Indent().WriteLine($"class Array({nonGenericName}, metaclass ={genericMetaclass}): ...");

            tw.WriteLine();
            tw.Indent().WriteLine("Array_1_T = typing.TypeVar('Array_1_T', covariant=True)");
            tw.Indent().WriteLine("class Array_1(Array_0, typing.Generic[Array_1_T]):...");
            tw.WriteLine();

            WriteType(tw, arrayType, nonGenericName);



        }
        private static void WriteClassHeader(
            ClassScope classScope,
            TextWriter tw,
            string className,
            List<string> classArguments = null,
            Dictionary<Type, string> genericAliases = null)
        {
            classArguments ??= new List<string>();
            var generics = ClassScope.AccessibleGenerics.ToList();

            var writtenAliases = new HashSet<string>();

            foreach (var accessibleGeneric in generics)
            {
                if (genericAliases != null && genericAliases.TryGetValue(accessibleGeneric, out var alias))
                {
                    if (writtenAliases.Contains(alias)) continue;
                    WriteTypeVariable(tw, accessibleGeneric, alias);
                    writtenAliases.Add(alias);
                }
                else
                {
                    WriteTypeVariable(tw, accessibleGeneric);
                }
            }

            if (generics.Count > 0)
            {
                var genericArgs = generics
                    .Select(it => genericAliases?.TryGetValue(it, out var alias) == true
                            ? alias
                            : it.ToPythonType())
                    .Distinct()
                    .CommaJoin();

                var genericDefinition = $"typing.Generic[{genericArgs}]";
                classArguments.Insert(0, genericDefinition);
            }

            var argumentsString = classArguments.CommaJoin();
            if (!string.IsNullOrEmpty(argumentsString))
                argumentsString = $"({argumentsString})";

            tw.Indent().WriteLine($"class {className}{argumentsString}:");
            classScope.EnterIndent();

            writtenAliases.Clear();

            if (!string.IsNullOrEmpty(classScope.OutsideAccessor))
            {
                foreach (var generic in generics)
                {
                    var name = generic.ToPythonType();
                    var outerName = name;

                    if (genericAliases?.TryGetValue(generic, out var alias) == true)
                        outerName = alias;

                    var aliasDef = $"{name} = {classScope.OutsideAccessor}{outerName}";

                    if (!writtenAliases.Contains(aliasDef))
                        tw.Indent().WriteLine(aliasDef);
                    writtenAliases.Add(aliasDef);
                }
            }

        }

        private static void WriteTypeOverload(TextWriter tw, string overloadClassName, List<Type> types)
        {
            tw.WriteLine();


            var externalGenerics = ClassScope.AccessibleGenerics.ToList();
            var newGenerics = Enumerable.Empty<Type>();
            using var classScope = new ClassScope(overloadClassName, newGenerics, false);
            WriteClassHeader(classScope, tw, overloadClassName, classArguments: new List<string>() { "abc.ABCMeta" });


            foreach (var type in types)
            {
                var args = type.GetGenericArguments().Skip(externalGenerics.Count).ToArray();

                var targetType = type.ToPythonType(false);

                var prefix = "Generic_";

                var typeVarList = args.Select(arg => $"{prefix}{arg.ToPythonType()}").ToList();
                var typeVarString = externalGenerics.Select(it => it.ToPythonType()).Concat(typeVarList).CommaJoin();

                var typeArgsList = typeVarList.Select(typeVar => $"typing.Type[{typeVar}]");
                var typeArgString = typeArgsList.CommaJoin();

                if (args.Length == 0)
                {
                    tw.Indent().WriteLine($"def __call__(self) -> {targetType}[{typeVarString}]: ...");
                    continue;
                }

                if (args.Length > 1) typeArgString = $"typing.Tuple[{typeArgString}]";

                foreach (var arg in args)
                {
                    WriteTypeVariable(tw, arg, $"{prefix}{arg.ToPythonType()}", writeVariance: false);
                }


                if (types.Count > 1) tw.Indent().WriteLine("@typing.overload");
                tw.Indent().WriteLine($"def __getitem__(self, types : {typeArgString}) -> typing.Type[{targetType}[{typeVarString}]]: ...");
            }

            tw.WriteLine();
        }

        private static void WriteType(TextWriter tw, Type type, string classNameOverride = null)
        {

            tw.WriteLine();
            if (type.IsEnum)
            {
                WriteEnum(tw, type);
                tw.WriteLine();
                return;
            }

            var className = classNameOverride ?? type.CleanName();



            var typeArguments = new List<Type>();

            if (type.IsGenericTypeDefinition)
            {
                typeArguments.AddRange(type.GetGenericArguments());
            }



            using (var classScope = new ClassScope(className, typeArguments, typeArguments.Any()))
            {

                var args = GetClassArguments(type);
                WriteClassHeader(classScope, tw, className, args);
                var wroteMember = false;

                wroteMember |= WriteConstructors(type, tw);
                wroteMember |= WriteFields(type, tw);
                wroteMember |= WriteProperties(type, tw);
                wroteMember |= WriteMethods(type, tw);
                wroteMember |= WriteNestedTypes(tw, type);
                wroteMember |= WriteIndexers(tw, type);

                if (!wroteMember) tw.Indent().WriteLine("pass");
            }

            tw.WriteLine();

        }

        private static bool WriteIndexers(TextWriter tw, Type type)
        {
            return false;
        }

        private static bool WriteNestedTypes(TextWriter tw, Type stubType)
        {
            var nestedTypeGroups = stubType.GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
                .OrderBy(it => it.Name)
                .GroupBy(it => it.NonGenericName());

            var wroteGroup = false;
            foreach (var typeGroup in nestedTypeGroups)
            {
                WriteTypeGroup(tw, typeGroup.Key, typeGroup);
                wroteGroup = true;
            }

            return wroteGroup;
        }

        private string GetDependencies(string nameSpace, string body)
        {
            using (var tw = new StringWriter { NewLine = NewLine })
            {
                var utilityDependencies = new List<string>();

                if (body.Contains("typing.")) utilityDependencies.Add("typing");
                if (body.Contains("clr.")) utilityDependencies.Add("clr");
                if (body.Contains("abc.")) utilityDependencies.Add("abc");

                if (utilityDependencies.Count > 0)
                {
                    tw.Indent().WriteLine("import " + utilityDependencies.CommaJoin());
                }

                var namespaces = PythonTypes.GetCurrentNamespaceDependencies();
                foreach (var ns in namespaces)
                {
                    tw.WriteLine($"import {ns}");
                }

                var depsByNamespace = PythonTypes.GetCurrentTypeDependencies()
                    .GroupBy(it => it.Namespace);

                var usedBaseArray = PythonTypes.CurrentUsedBaseArray;
                var usedGenericArray = PythonTypes.CurrentUsedGenericArray;

                foreach (var group in depsByNamespace)
                {
                    if (group.Key == nameSpace) continue;
                    var types = group.Select(it => GetRootType(it).ToPythonType(false)).Distinct().ToList();

                    var arrayType = typeof(Array);
                    var arrayTypeStr = arrayType.ToPythonType(false);
                    if (group.Key == arrayType.Namespace && types.Contains(arrayTypeStr))
                    {
                        var index = types.IndexOf(arrayTypeStr);
                        var arrayTypes = new List<string>();
                        if (usedBaseArray) arrayTypes.Add("Array");
                        if (usedGenericArray) arrayTypes.Add("Array_1");
                        types[index] = arrayTypes.CommaJoin();
                    }

                    var typesStr = types.CommaJoin();
                    tw.WriteLine($"from {group.Key} import {typesStr}");
                }

                return tw.ToString();
            }
        }


        private static Type GetRootType(Type type)
        {
            if (type.DeclaringType == null) return type;
            return GetRootType(type.DeclaringType);
        }

        private static List<string> GetClassArguments(Type type)
        {
            var args = new List<string>();

            var baseType = type.BaseType;

            if (baseType != null && baseType != typeof(object) && baseType != typeof(ValueType))
            {
                var baseName = baseType.ToPythonType();
                if (baseType.IsOverloadedNonGenericType()) baseName += "_0";
                args.Add(baseName);
            }

            var interfaces = type.GetInterfaces().OrderBy(GetInterfaceDepth).Reverse().ToList();


            var baseInterfaces = new HashSet<Type>();
            if (baseType != null) foreach (var i in baseType.GetInterfaces()) baseInterfaces.Add(i);
            foreach (var i in interfaces)
                foreach (var i2 in i.GetInterfaces())
                {
                    if (i != i2) baseInterfaces.Add(i2);
                }


            foreach (var i in interfaces)
            {
                if (!i.IsVisible) continue;
                if (baseInterfaces.Contains(i)) continue;

                var baseName = i.ToPythonType();
                if (i.IsOverloadedNonGenericType()) baseName += "_0";
                args.Add(baseName);
            }
            if (type.IsInterface)
            {
                args.Add("typing.Protocol");
            }
            else if (type.IsAbstract && baseType?.IsAbstract != true) args.Add("abc.ABC");


            return args;
        }

        private static readonly Dictionary<Type, int> InterfaceDepthCache = new Dictionary<Type, int>();
        private static int GetInterfaceDepth(Type t)
        {
            if (InterfaceDepthCache.TryGetValue(t, out var val)) return val;
            var interfaces = t.GetInterfaces();
            if (interfaces.Length == 0) return 0;
            var max = interfaces.Max(GetInterfaceDepth);
            var depth = max + 1;
            InterfaceDepthCache[t] = depth;
            return depth;
        }

        private static void WriteTypeVariable(TextWriter tw, Type typeVariable, string customName = null, bool writeVariance = true)
        {

            var varName = customName ?? $"{typeVariable.ToPythonType()}";
            tw.Indent().Write($"{varName} = typing.TypeVar('{varName}'");

            if (writeVariance)
            {
                var covariant = typeVariable.GenericParameterAttributes.HasFlag(GenericParameterAttributes.Covariant);
                var contravariant = typeVariable.GenericParameterAttributes.HasFlag(GenericParameterAttributes.Contravariant);


                if (contravariant) tw.Write(", contravariant=True");
                else if (covariant) tw.Write(", covariant=True");
            }

            var bound = GetTypeVarBound(typeVariable);

            if (!string.IsNullOrEmpty(bound)) tw.Write(", bound=" + bound);
            tw.WriteLine(")");
        }

        private static string GetTypeVarBound(Type typeVariable)
        {
            var constraints = typeVariable.GetGenericParameterConstraints().ToList();
            constraints.RemoveAll(it => it == typeof(ValueType));

            if (constraints.Count <= 1) return null;

            if (constraints.Count == 1) return constraints[0].ToPythonType();

            var types = constraints.Select(it => it.ToPythonType()).CommaJoin();
            return $"Union[{types}]";

        }


        private static bool WriteMethods(Type stubType, TextWriter tw)
        {

            bool IsPropertyAccessor(MethodInfo it) =>
                it.IsSpecialName && (
                    it.Name.StartsWith("set_") ||
                    it.Name.StartsWith("get_") ||
                    it.Name.StartsWith("add_") ||
                    it.Name.StartsWith("remove_"));


            bool IsMethodGroup(List<MethodInfo> methodGroup)
            {
                return !methodGroup.Any(IsOperator) &&
                        methodGroup.Count > 1 ||
                        methodGroup.Any(it => it.IsGenericMethodDefinition);
            }

            // methods
            // sort for consistent output
            var methods = stubType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

            var methodGroups = methods
                .OrderBy(it => it, new MethodComparer())
                .Where(it => !IsPropertyAccessor(it))
                .GroupBy(it => it.NonGenericName())
                .Where(it => it.Any(method => method.DeclaringType == stubType))
                .OrderBy(infos => IsMethodGroup(infos.ToList()))
                .ToArray();

            bool didWrite = false;
            foreach (var methodGroup in methodGroups)
            {
                if (IsMethodGroup(methodGroup.ToList()))
                {
                    var name = methodGroup.Key;

                    if (stubType.IsInterface && methodGroup.Any(it=>it.IsStatic && it.IsAbstract))
                        continue;

                    tw.Indent().WriteLine($"# Skipped {name} due to it being static, abstract and generic.");
                    WriteMethodGroup(tw, stubType, methodGroup, methodGroup.Key);
                    didWrite = true;
                }
                else
                {
                    foreach (var method in methodGroup)
                    {
                        didWrite |= WriteSimpleMethod(tw, method, methodGroup.Count() > 1);
                    }
                }
            }

            if (stubType == typeof(IEnumerable))
            {
                tw.Indent().WriteLine($"def __iter__(self) -> typing.Iterator[typing.Any]: ...");
            }
            else if (stubType == typeof(IEnumerable<>))
            {
                var elementType = stubType.GetGenericArguments()[0].ToPythonType();
                tw.Indent().WriteLine($"def __iter__(self) -> typing.Iterator[{elementType}]: ...");
            }

            return didWrite;
        }

        private static bool IsOperator(MethodBase method) => method.IsSpecialName && method.Name.StartsWith("op_");

        private static bool WriteConstructors(Type stubType, TextWriter tw)
        {
            // constructors
            // sort for consistent output
            var constructors = stubType.GetConstructors(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .OrderBy(it => it.Name + string.Join("_", it.GetParameters().Select(p => p.Name)))
                .ToArray();


            var existingSignatures = new HashSet<string>();
            List<ConstructorInfo> ConstructorsToAdd = new List<ConstructorInfo>();
            foreach (var constructor in constructors)
            {

                var signature = GetUniqueMethodSignature(constructor);

                if (existingSignatures.Contains(signature))
                {
                    var dotnetParams = constructor.GetParameters().Select(it => $"{it.Name} : {it.ParameterType.Name}");
                    var dotnetSignature = dotnetParams.CommaJoin();
                    tw.Indent().WriteLine($"# Constructor {constructor.Name}({dotnetSignature}) was skipped since it collides with above method");
                    continue;
                }
                existingSignatures.Add(signature);

                ConstructorsToAdd.Add(constructor);
            }

            foreach (var constructorInfo in ConstructorsToAdd)
            {
                WriteSimpleMethod(tw, constructorInfo, ConstructorsToAdd.Count > 1);
            }

            return ConstructorsToAdd.Count > 0;
        }

        private static bool WriteProperties(Type stubType, TextWriter tw)
        {
            var properties = stubType.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .OrderBy(it => it.Name)
                .ToArray();

            foreach (var property in properties)
            {
                if (PythonTypes.IsReservedWord(property.Name))
                {
                    tw.Indent().WriteLine($"# Skipped property {property.Name} since it is a reserved python word. Use reflection to access.");
                    continue;
                }

                var isStatic = property.GetAccessors(true)[0].IsStatic;
                var firstParam = isStatic ? "cls" : "self";

                if (isStatic) tw.Indent().WriteLine("@classmethod");
                tw.Indent().WriteLine("@property");
                var propType = property.PropertyType.ToPythonType();
                var getterType = property.CanRead ? propType : "None";
                tw.Indent().WriteLine($"def {property.Name}({firstParam}) -> {getterType}: ...");

                if (property.CanWrite)
                {
                    if (isStatic) tw.Indent().WriteLine("@classmethod");
                    tw.Indent().WriteLine($"@{property.Name}.setter");
                    tw.Indent().WriteLine($"def {property.Name}({firstParam}, value: {propType}) -> {getterType}: ...");
                }
            }

            return properties.Length > 0;
        }

        private static bool WriteFields(Type stubType, TextWriter tw)
        {
            var fields = stubType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .OrderBy(it => it.Name)
                .ToArray();

            foreach (var field in fields)
            {
                var type = field.FieldType.ToPythonType();
                tw.Indent().WriteLine($"{field.Name} : {type}");
            }

            return fields.Length > 0;
        }

        static IEnumerable<Type> GetAllUsedGenerics(Type type)
        {
            var result = Enumerable.Empty<Type>();
            if (type == null) return result;

            if (type.IsGenericParameter)
                result = result.Append(type);

            var generics = type.GetGenericArguments().SelectMany(GetAllUsedGenerics);
            result = result.Concat(generics);

            if (type.HasElementType)
                result = result.Concat(GetAllUsedGenerics(type.GetElementType()));

            return result.Distinct();
        }


        static IEnumerable<Type> GetAllUsedGenerics(MethodInfo methodInfo) => methodInfo
            .GetParameters()
            .Select(it => it.ParameterType)
            .Append(methodInfo.ReturnType)
            .SelectMany(GetAllUsedGenerics)
            .Distinct();


        static IEnumerable<Type> GetAllUsedGenerics(IEnumerable<MethodInfo> methodInfos) =>
            methodInfos.SelectMany(GetAllUsedGenerics).Distinct();



        private static void WriteMethodGroup(TextWriter tw, Type containingType, IEnumerable<MethodInfo> methodGroup, string methodName)
        {
            var infos = methodGroup.OrderBy(it => it, new MethodComparer()).ToList();

            tw.WriteLine();


            var className = $"{methodName}_MethodGroup";

            var currentGenerics = ClassScope.AccessibleGenerics.Select(it => it.ToPythonType()).CommaJoin();

            if (!string.IsNullOrEmpty(currentGenerics)) currentGenerics = $"[{currentGenerics}]";
            tw.Indent().WriteLine($"{methodName} : {className}{currentGenerics}");

            using (var classScope = new ClassScope(className, Enumerable.Empty<Type>(), false))
            {
                WriteClassHeader(classScope, tw, className);
                // We want to merge methods with the same amount of parameters with the same bounds
                // (since the python type system can't distinguish between them)
                string GetBoundsKey(MethodInfo it) =>
                    it.GetGenericArguments().Select(GetTypeVarBound).CommaJoin();

                var genericMethodGroups = infos
                    .Where(it => it.IsGenericMethodDefinition)
                    .GroupBy(GetBoundsKey)
                    .ToList();

                var hasGenericOverloads = genericMethodGroups.Count > 1;

                foreach (var methods in genericMethodGroups)
                {
                    WriteGenericMethodAccessors(tw, methods, hasGenericOverloads);
                }

                var callsToAdd = GetMethodCallers(infos, true);


                foreach (var call in callsToAdd)
                {
                    tw.Indent().WriteLine(call);
                }


            }
            tw.WriteLine();
        }


        private static List<string> GetMethodCallers(List<MethodInfo> infos, bool skipHardGenerics)
        {
            var allLines = new List<string>();
            var callsToAdd = new List<string>();
            var existingSignatures = new HashSet<string>();
            foreach (var method in infos)
            {
                var returnType = method.ReturnType.ToPythonType();

                if (skipHardGenerics && method.IsGenericMethodDefinition)
                    continue;

                var parameters = GetParameters(method, true);
                var callMethod = $"def __call__({parameters}) -> {returnType}:...";

                var signature = GetUniqueMethodSignature(method);

                if (existingSignatures.Contains(signature))
                {
                    var dotnetParams = method.GetParameters().Select(it => $"{it.Name} : {it.ParameterType.Name}");
                    var dotnetSignature = dotnetParams.CommaJoin();
                    callsToAdd.Add(
                        $"# Method {method.Name}({dotnetSignature}) was skipped since it collides with above method");
                    continue;
                }

                existingSignatures.Add(signature);
                callsToAdd.Add(callMethod);
            }


            var actualMethodCount = callsToAdd.Count(it => !it.Trim().StartsWith("#"));

            foreach (var call in callsToAdd)
            {
                if (actualMethodCount > 1 && !call.StartsWith("#"))
                    allLines.Add("@typing.overload");

                allLines.Add(call);
            }

            return allLines;
        }

        private static bool IsGenericMethodCallable(MethodInfo method)
        {
            var genericArguments = method.GetGenericArguments();

            // Skip writing the __call__ implementation if the generic method is unbound by its parameters,
            // That is to say. The method would not be able to infer it's generic parameters from the call parameters
            // CLR would require the user to specify the generic parameters in this case

            var parameterTypes = method.GetParameters().Select(it => it.ParameterType).ToList();
            var unboundGenerics = genericArguments.Except(parameterTypes);
            if (unboundGenerics.Any()) return false;

            // This one is a little sketchy. Python generic methods require that type variables
            // be used more than once in Method signatures. In CLR, this is not the case, since the
            // Type of a single parameter can sometimes be used to infer the generic parameter of the method,
            // which can point to a different implementation/specialization.
            // For now, I will assume that Python.NET uses some runtime information in the object reference to 
            // dispatch the correct method. And we can use the type bound to type the method parameter.
            // Todo: I've skipped this complication. For now we'll force the user to pass in the generic parameters

            var usedGenericParams = GetAllUsedGenerics(method);
            var usedArgs = genericArguments.Intersect(usedGenericParams);
            return usedArgs.Count() != 1;
        }

        private static string GetUniqueMethodSignature(MethodBase method)
        {
            var methodParams = method.GetParameters().Select(it =>
            {
                // In python, float/bool/int overloads are conflicting with one another.
                var paramName = it.ParameterType.ToPythonType();
                if (paramName == "float") paramName = "int";
                if (paramName == "bool") paramName = "int";
                if (paramName.Contains("[float]")) paramName = paramName.Replace("[float]", "[int]");
                if (paramName.Contains("[bool]")) paramName = paramName.Replace("[bool]", "[int]");
                return paramName;
            });
            var signature = methodParams.CommaJoin();
            return signature;
        }

        private static void WriteGenericMethodAccessors(
            TextWriter tw,
            IEnumerable<MethodInfo> methods,
            bool hasGenericOverloads
            )
        {
            var methodInfos = methods.ToList();

            // use first method to get info
            var templateMethod = methodInfos[0];
            var templateArguments = templateMethod.GetGenericArguments();
            var methodClassName = templateMethod.CleanName();

            var aliasDictionary = new Dictionary<Type, string>();
            var aliases = new List<string>();


            for (var i = 0; i < templateArguments.Length; i++)
            {
                var alias = $"{methodClassName}_T{i + 1}";
                var positionalParams = methodInfos.Select(method => method.GetGenericArguments()[i]).ToList();
                aliases.Add(alias);
                foreach (var param in positionalParams)
                    aliasDictionary[param] = alias;
            }

            var outerGenerics = ClassScope.AccessibleGenerics;

            var indexerTypes = aliases.Select(it => $"typing.Type[{it}]");
            var typeVarsString = indexerTypes.CommaJoin();
            var indexerArgs = templateArguments.Length == 1 ? typeVarsString : $"typing.Tuple[{typeVarsString}]";

            var genericArguments = outerGenerics.Select(it => it.ToPythonType()).Concat(aliases);

            var returnTypeStr = $"{methodClassName}[{genericArguments.CommaJoin()}]";

            if (hasGenericOverloads) tw.Indent().WriteLine("@typing.overload");
            tw.Indent().WriteLine($"def __getitem__(self, t:{indexerArgs}) -> {returnTypeStr}: ...");


            tw.WriteLine();


            using (var classScope = new ClassScope(methodClassName, aliasDictionary.Keys, false))
            {

                WriteClassHeader(classScope, tw, methodClassName, genericAliases: aliasDictionary);
                var callLines = GetMethodCallers(methodInfos, false);
                foreach (var line in callLines)
                    tw.Indent().WriteLine(line);
            }

            tw.WriteLine();

        }


        private static bool WriteSimpleMethod(TextWriter tw, MethodBase method, bool isOverload = false)
        {
            var isOperator = IsOperator(method);
            var isStatic = method.IsStatic && !isOperator;


            var methodName = method.IsConstructor ? "__init__" : method.Name;
            if (methodName == "<Clone>$") return false;
            if (isOperator)
            {
                methodName = ConvertOperatorName(method.Name);
                if (methodName == null)
                {
                    var signature = method.GetParameters().Select(it => it.Name + ": " + it.ParameterType.Name).CommaJoin();
                    tw.Indent().WriteLine($"# Operator not supported {method.Name}({signature})");
                    return false;
                }
            }

            var returnType = method is MethodInfo mi ? mi.ReturnType.ToPythonType() : "None";

            var parameters = GetParameters(method, !isStatic);


            // ReSharper disable StringLiteralTypo - python decorator
            if (isOverload) tw.Indent().WriteLine("@typing.overload");
            if (isStatic) tw.Indent().WriteLine("@staticmethod");
            if (method.IsAbstract) tw.Indent().WriteLine("@abc.abstractmethod");
            // ReSharper enable StringLiteralTypo - python decorator

            tw.Indent().WriteLine($"def {methodName}({parameters}) -> {returnType}: ...");
            return true;
        }

        private static string ConvertOperatorName(string methodName)
        {
            switch (methodName)
            {
                case "op_Equality": return "__eq__";
                case "op_Inequality": return "__ne__";
                case "op_GreaterThan": return "__gt__";
                case "op_LessThan": return "__lt__";
                case "op_GreaterThanOrEqual": return "__ge__";
                case "op_LessThanOrEqual": return "__le__";
                case "op_BitwiseAnd": return "__and__";
                case "op_BitwiseOr": return "__or__";
                case "op_Addition": return "__add__";
                case "op_Subtraction": return "__sub__";
                case "op_Division": return "__truediv__";
                case "op_Modulus": return "__mod__";
                case "op_Multiply": return "__mul__";
                case "op_LeftShift": return "__lshift__";
                case "op_RightShift": return "__rshift__";
                case "op_ExclusiveOr": return "__xor__";
                case "op_UnaryNegation": return "__neg__";
                case "op_UnaryPlus": return "__pos__";
                case "op_OnesComplement": return "__invert__";
                case "op_LogicalNot": return null;
                case "op_False": return null;
                case "op_True": return null;
                case "op_Increment": return null;
                case "op_Decrement": return null;
            }

            return null;
        }

        private static string GetParameters(MethodBase method, bool includeSelf)
        {
            string GetParameter(ParameterInfo it)
            {
                var name = PythonTypes.SafePythonName(it.Name);
                var type = it.ParameterType.ToPythonType();
                var defaultValue = it.HasDefaultValue ? " = ..." : "";
                return $"{name}: {type}{defaultValue}";
            }

            var parameters = method.GetParameters();
            var pythonParams = parameters.Select(GetParameter);
            if (includeSelf) pythonParams = pythonParams.Prepend("self");
            return pythonParams.CommaJoin();
        }

        private static void WriteEnum(TextWriter tw, Type stubType)
        {
            var underlyingType = stubType.GetEnumUnderlyingType().ToPythonType();
            tw.Indent().WriteLine($"class {stubType.Name}(typing.SupportsInt):");
            using var _ = new IndentScope();
            
            tw.Indent().WriteLine("@typing.overload");
            tw.Indent().WriteLine($"def __init__(self, value : {underlyingType}) -> None: ...");
            tw.Indent().WriteLine("@typing.overload");
            tw.Indent().WriteLine($"def __init__(self, value : {underlyingType}, force_if_true: {typeof(bool).ToPythonType()}) -> None: ...");
            tw.Indent().WriteLine($"def __int__(self) -> int: ...");
            tw.Indent().WriteLine();
            tw.Indent().WriteLine("# Values:");
            var names = Enum.GetNames(stubType);
            var values = Enum.GetValues(stubType);

            for (var i = 0; i < names.Length; i++)
            {
                var name = names[i];
                name = PythonTypes.SafePythonName(name);

                var val = Convert.ChangeType(values.GetValue(i), Type.GetTypeCode(stubType));
                tw.Indent().WriteLine($"{name} : {stubType.ToPythonType()} # {val}");
            }

        }
    }
}
