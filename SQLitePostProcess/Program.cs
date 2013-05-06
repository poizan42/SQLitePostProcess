using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;
using System.Runtime.CompilerServices;
using Mono.Cecil.Cil;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SQLitePostProcess
{
  class Program
  {
    const string SQLITE_DLL = "SQLite.Interop.dll";

    static void Main(string[] args)
    {
      string srcAssembly = args[0];
      string dstAssembly = args[1];
      var assembly = AssemblyDefinition.ReadAssembly(args[0]);
			FindAndReplaceNativeMethods(assembly);
      assembly.Write(dstAssembly);
    }

    private static void FindAndReplaceNativeMethods(AssemblyDefinition assembly)
    {
      foreach (var module in assembly.Modules)
      {
        foreach (var type in module.Types)
        {
          if (type.FullName == "System.Data.SQLite.UnsafeNativeMethods")
          {
						AddGetProcAddress(type);
            ReplaceNativeMethods(type);
            return;
          }
        }
      }
    }

    private static void AddGetProcAddress(TypeDefinition type)
    {
			var module = type.Module;
      MethodDefinition getProcAddressDef = new MethodDefinition("GetProcAddress", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.IntPtr);
      getProcAddressDef.IsPInvokeImpl = true;
      var kernel32ref = module.ModuleReferences.FirstOrDefault(mr => mr.Name.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase));
      if (kernel32ref == null)
      {
        kernel32ref = new ModuleReference("kernel32.dll");
        module.ModuleReferences.Add(kernel32ref);
      }
      getProcAddressDef.IsPreserveSig = true;
      getProcAddressDef.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvWinapi | PInvokeAttributes.NoMangle | PInvokeAttributes.CharSetAnsi |
        PInvokeAttributes.SupportsLastError, "GetProcAddress", kernel32ref);
      getProcAddressDef.IsIL = false;
      getProcAddressDef.Parameters.Add(new ParameterDefinition("hModule", ParameterAttributes.None, module.TypeSystem.IntPtr));
      getProcAddressDef.Parameters.Add(new ParameterDefinition("lpProcName", ParameterAttributes.None, module.TypeSystem.String));
      type.Methods.Add(getProcAddressDef);
    }

    private static void ReplaceNativeMethods(TypeDefinition type)
    {
			var compilerGeneratedAttrRef = type.Module.Import(typeof(CompilerGeneratedAttribute));
      MethodReference compilerGeneratedAttrCtor = CecilUtils.ImportInstanceMethodRef(type.Module, compilerGeneratedAttrRef, ".ctor");
      MethodDefinition cctor = GetOrCreateCctorWithRet(type);
      int initInjectionPoint = InjectLibraryInit(cctor);
      Action<Instruction> emitInit = i => cctor.Body.Instructions.Insert(initInjectionPoint++, i);
      List<MethodDefinition> nativeMethods = type.Methods.Where(m => m.IsPInvokeImpl && m.PInvokeInfo.Module != null &&
        m.PInvokeInfo.Module.Name == SQLITE_DLL).ToList();
      for (int idx = 0; idx < nativeMethods.Count; idx++)
      {
        var method = nativeMethods[idx];
        TypeDefinition methodDelegateType = CecilUtils.CreateDelegateFromMethod(type.Module, "", "<>delegate_" + method.Name, method, TypeAttributes.NestedPrivate);
				methodDelegateType.CustomAttributes.Add(new CustomAttribute(compilerGeneratedAttrCtor));
        type.NestedTypes.Add(methodDelegateType);
				FieldAttributes fieldAttrs = FieldAttributes.CompilerControlled | FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly;
        FieldDefinition funcPtrField = new FieldDefinition("<>funcptr_" + method.Name, fieldAttrs, methodDelegateType);
        funcPtrField.CustomAttributes.Add(new CustomAttribute(compilerGeneratedAttrCtor));
        type.Fields.Add(funcPtrField);
        InjectFuncPtrInit(method, funcPtrField, emitInit, idx == nativeMethods.Count - 1);
        WriteMethodStub(method, funcPtrField);
      }
    }

    private static void EmitArchsInit(MethodBody body, FieldReference archRef, Action<Instruction> emit)
    {
      var module = body.Method.Module;
      GenericInstanceType dictStrStrRef = (GenericInstanceType)archRef.FieldType;
      TypeReference dictOpenRef = dictStrStrRef.ElementType;
      GenericInstanceType iEqCompStrRef = new GenericInstanceType(module.Import(typeof(IEqualityComparer<>)));
      iEqCompStrRef.GenericArguments.Add(dictOpenRef.GenericParameters[0]);
      MethodReference dictStrStrCtor = CecilUtils.ImportInstanceMethodRef(module, dictStrStrRef, ".ctor", null, iEqCompStrRef);
      MethodReference dictAddRef = CecilUtils.ImportInstanceMethodRef(module, dictStrStrRef, "Add", null, dictOpenRef.GenericParameters[0], dictOpenRef.GenericParameters[1]);

			// Variables
      body.Variables.Add(new VariableDefinition(dictStrStrRef));
      int varIdx = body.Variables.Count - 1;
			Instruction varSt = CecilUtils.ShortestStloc(varIdx);
			Instruction varLd = CecilUtils.ShortestLdloc(varIdx);

			emit(Instruction.Create(OpCodes.Ldnull));
			emit(Instruction.Create(OpCodes.Newobj, dictStrStrCtor));
      emit(varSt.Clone());
      emit(varLd.Clone());
      emit(Instruction.Create(OpCodes.Stsfld, archRef));
      Action<string, string> emitAddPair = (k, v) =>
      {
	      emit(varLd.Clone());
				emit(Instruction.Create(OpCodes.Ldstr, k));
				emit(Instruction.Create(OpCodes.Ldstr, v));
        emit(Instruction.Create(OpCodes.Callvirt, dictAddRef));
      };
      emitAddPair("x86", "Win32");
      emitAddPair("AMD64", "x64");
      emitAddPair("IA64", "Itanium");
      emitAddPair("ARM", "WinCE");
    }

    private static int InjectLibraryInit(MethodDefinition cctor)
    {
      var type = cctor.DeclaringType;
      var module = type.Module;
			var body = cctor.Body;
      body.InitLocals = true;

      int insertionPoint = -1;
      for (int i = 0; i < body.Instructions.Count; i++)
      { 
        Instruction ins = body.Instructions[i];
        if (ins.OpCode.Code == OpCodes.Call.Code && ((MethodReference)ins.Operand).Name == "Initialize")
        {
          insertionPoint = i;
          break;
        }
      }
      if (insertionPoint != -1)
        body.Instructions.RemoveAt(insertionPoint);
			else
				insertionPoint = body.Instructions.Count - 1;
			FieldReference archRef = type.Fields.First(f => f.Name == "processorArchitecturePlatforms");
			Action<Instruction> emit = i => body.Instructions.Insert(insertionPoint++, i);
			EmitArchsInit(body, archRef, emit);
      
      FieldReference sqliteModuleRef = new FieldReference("_SQLiteModule", module.TypeSystem.IntPtr, type);
      MethodReference loadLibraryRef = CecilUtils.CreateStaticMethodRef(type, "LoadLibrary", module.TypeSystem.IntPtr, module.TypeSystem.String);
			MethodReference getEnvironmentVariableRef = module.Import(new Func<string, string>(Environment.GetEnvironmentVariable).Method);
      MethodReference getPlatformNameRef = CecilUtils.CreateStaticMethodRef(type, "GetPlatformName", module.TypeSystem.String, module.TypeSystem.String);
      MethodReference strConcat3Ref = module.Import(new Func<string, string, string, string>(String.Concat).Method);
      MethodReference strFormat1Ref = module.Import(new Func<string, object, string>(String.Format).Method);
			TypeReference traceRef = module.Import(typeof(System.Diagnostics.Trace));
      MethodReference traceWriteLineStrRef = CecilUtils.ImportStaticMethodRef(module, traceRef, "WriteLine", null, module.TypeSystem.String);
      
      body.Variables.Add(new VariableDefinition("dllName", module.TypeSystem.String));
      int dllNameIdx = body.Variables.Count - 1;
      var dllNameSt = CecilUtils.ShortestStloc(dllNameIdx);
      var dllNameLd = CecilUtils.ShortestLdloc(dllNameIdx);
      emit(Instruction.Create(OpCodes.Ldstr, "SQLite.Interop"));
      emit(Instruction.Create(OpCodes.Ldsfld, new FieldReference("PROCESSOR_ARCHITECTURE", module.TypeSystem.String, type)));
      emit(Instruction.Create(OpCodes.Call, getEnvironmentVariableRef));
      emit(Instruction.Create(OpCodes.Call, getPlatformNameRef));
			emit(Instruction.Create(OpCodes.Ldstr, ".dll"));
			//Stack: "SQLite.Interop", (platform name), ".dll"
      emit(Instruction.Create(OpCodes.Call, strConcat3Ref));
      emit(dllNameSt.Clone());

      emit(Instruction.Create(OpCodes.Ldstr, "Trying to load native SQLite library \"{0}\"..."));
      emit(dllNameLd.Clone());
      emit(Instruction.Create(OpCodes.Call, strFormat1Ref));
      emit(Instruction.Create(OpCodes.Call, traceWriteLineStrRef));
      
      emit(dllNameLd.Clone());
      emit(Instruction.Create(OpCodes.Call, loadLibraryRef));
      emit(Instruction.Create(OpCodes.Dup)); //leave a copy on the stack
      emit(Instruction.Create(OpCodes.Stsfld, sqliteModuleRef)); 
      return insertionPoint;
    }

    private static void InjectFuncPtrInit(MethodDefinition method, FieldDefinition funcPtrField, Action<Instruction> emit, bool isLast)
    {
			//TODO: Error check
      var type = funcPtrField.DeclaringType;
			var delegateRef = funcPtrField.FieldType;
      var module = type.Module;
      var getProcAddrRef = CecilUtils.CreateStaticMethodRef(type, "GetProcAddress", module.TypeSystem.IntPtr, module.TypeSystem.IntPtr, module.TypeSystem.String);
			var getDelegateForFunctionPointerRef = module.Import(new Func<IntPtr, Type, Delegate>(Marshal.GetDelegateForFunctionPointer).Method);
			var getTypeFromHandleRef = module.Import(new Func<RuntimeTypeHandle, Type>(Type.GetTypeFromHandle).Method);
			// the library is currently at the top of the stack
			if (!isLast)
				emit(Instruction.Create(OpCodes.Dup)); // Keep a copy of the library pointer at the top of the stack
      //GetProcAddr(sqliteLibrary, method)
      emit(Instruction.Create(OpCodes.Ldstr, GetEntryPoint(method)));
      emit(Instruction.Create(OpCodes.Call, getProcAddrRef));
			//Marshal.GetDelegateForFunctionPointer(ptr, typeof(DELEGATE))
      emit(Instruction.Create(OpCodes.Ldtoken, delegateRef));
      emit(Instruction.Create(OpCodes.Call, getTypeFromHandleRef));
      emit(Instruction.Create(OpCodes.Call, getDelegateForFunctionPointerRef));

      emit(Instruction.Create(OpCodes.Castclass, delegateRef));
      emit(Instruction.Create(OpCodes.Stsfld, funcPtrField));
    }

    private static MethodDefinition GetOrCreateCctorWithRet(TypeDefinition type)
    {
      MethodAttributes cctorAttributes = MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Static;
      MethodDefinition cctor = type.Methods.FirstOrDefault(m => (m.Attributes & cctorAttributes) == cctorAttributes && m.Name == ".cctor");
      if (cctor == null)
      {
        cctor = new MethodDefinition(".cctor", cctorAttributes | MethodAttributes.Private | MethodAttributes.HideBySig, type.Module.TypeSystem.Void);
        var il = cctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ret);
        type.Methods.Add(cctor);
      }
      return cctor;
    }
    
    private static string GetEntryPoint(MethodDefinition method)
    {
      if (method.IsPInvokeImpl && method.HasPInvokeInfo && !String.IsNullOrEmpty(method.PInvokeInfo.EntryPoint))
        return method.PInvokeInfo.EntryPoint;
      else
        return method.Name;
    }

    private static void WriteMethodStub(MethodDefinition method, FieldDefinition funcPtrField)
    {
      var delegateRef = funcPtrField.FieldType;
      var delegateInvokeRef = delegateRef.Resolve().Methods.First(m => m.Name == "Invoke");
			method.PInvokeInfo = null;
			method.IsPInvokeImpl = false;
      method.IsManaged = true;
			method.IsIL = true;
      method.IsPreserveSig = false;
			var il = method.Body.GetILProcessor();
      il.Emit(OpCodes.Ldsfld, funcPtrField);
      foreach (var param in method.Parameters)
      {
        il.Append(CecilUtils.ShortestLdarg(param));
      }
      il.Emit(OpCodes.Call, delegateInvokeRef);
      il.Emit(OpCodes.Ret);
    }

  }
}
