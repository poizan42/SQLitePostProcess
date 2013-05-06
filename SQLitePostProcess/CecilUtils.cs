using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;
using System.Runtime.InteropServices;
using Mono.Cecil.Cil;

namespace SQLitePostProcess
{
  public static class CecilUtils
  {
    public static void CopyReturnTypeTo(MethodReturnType dst, MethodReturnType src)
    {
      dst.Attributes = src.Attributes;
      dst.Constant = src.Constant;
      dst.CustomAttributes.Clear();
      foreach (var attr in src.CustomAttributes)
        dst.CustomAttributes.Add(attr);
      dst.HasConstant = src.HasConstant;
      dst.HasDefault = src.HasDefault;
      dst.HasFieldMarshal = src.HasFieldMarshal;
      dst.MarshalInfo = src.MarshalInfo;
      dst.ReturnType = src.ReturnType;
    }

    public static TypeDefinition CreateDelegate(ModuleDefinition module, string @namespace, string name, 
      TypeReference returnType, IEnumerable<ParameterDefinition> parameters = null)
    {
      return CreateDelegate(module, @namespace, name, returnType: new MethodReturnType(null) { ReturnType = returnType },
        parameters: parameters);
    }

    public static TypeDefinition CreateDelegate(ModuleDefinition module, string @namespace, string name, 
      TypeAttributes delegateVisibility, TypeReference returnType,
      IEnumerable<ParameterDefinition> parameters = null)
    {
      return CreateDelegate(module, @namespace, name, delegateVisibility, new MethodReturnType(null) { ReturnType = returnType },
        parameters);
    }

    public static TypeDefinition CreateDelegate(ModuleDefinition module, string @namespace, string name,
      TypeAttributes delegateVisibility = TypeAttributes.NotPublic, MethodReturnType returnType = null,
      IEnumerable<ParameterDefinition> parameters = null)
    {
      if ((delegateVisibility & TypeAttributes.VisibilityMask) != delegateVisibility)
        throw new ArgumentException("Parameter may only contain visibility attributes.", "delegateVisibility");
      var objectRef = module.TypeSystem.Object;
      var voidRef = module.TypeSystem.Void;
      var intPtrRef = module.TypeSystem.IntPtr;
      var multicastDelegateRef = module.Import(typeof(MulticastDelegate));
      var asyncCallbackRef = module.Import(typeof(AsyncCallback));
      var iAsyncResultRef = module.Import(typeof(IAsyncResult));
      
      if (returnType == null)
        returnType = new MethodReturnType(null) { ReturnType = voidRef };
      if (parameters == null)
        parameters = Enumerable.Empty<ParameterDefinition>();

      TypeAttributes typeAttributes = delegateVisibility | TypeAttributes.AnsiClass | TypeAttributes.Sealed;
      TypeDefinition td = new TypeDefinition(@namespace, name, typeAttributes, multicastDelegateRef);
      MethodAttributes ctorAttributes = MethodAttributes.Public | MethodAttributes.HideBySig |
        MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
      //.ctor
      MethodDefinition ctor = new MethodDefinition(".ctor", ctorAttributes, voidRef) {
        IsRuntime = true, IsManaged = true
      };
      ctor.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, objectRef));
      ctor.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, intPtrRef));
      td.Methods.Add(ctor);
      //Invoke
      MethodAttributes methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig |
        MethodAttributes.NewSlot | MethodAttributes.Virtual;
      MethodDefinition InvokeMethod = new MethodDefinition("Invoke", methodAttributes, returnType.ReturnType) {
        IsRuntime = true, IsManaged = true
      };
      CopyReturnTypeTo(InvokeMethod.MethodReturnType, returnType);
      foreach (var p in parameters)
        InvokeMethod.Parameters.Add(p);
      td.Methods.Add(InvokeMethod);
      //BeginInvoke
      MethodDefinition BeginInvokeMethod = new MethodDefinition("BeginInvoke", methodAttributes, iAsyncResultRef) {
        IsRuntime = true, IsManaged = true
      };
      foreach (var p in parameters)
        BeginInvokeMethod.Parameters.Add(p);
      BeginInvokeMethod.Parameters.Add(new ParameterDefinition("callback", ParameterAttributes.None, asyncCallbackRef));
      BeginInvokeMethod.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, objectRef));
      td.Methods.Add(BeginInvokeMethod);
      //EndInvoke
      MethodDefinition EndInvokeMethod = new MethodDefinition("EndInvoke", methodAttributes, returnType.ReturnType) {
        IsRuntime = true, IsManaged = true
      };
      CopyReturnTypeTo(EndInvokeMethod.MethodReturnType, returnType);
      EndInvokeMethod.Parameters.Add(new ParameterDefinition("result", ParameterAttributes.None, iAsyncResultRef));
      td.Methods.Add(EndInvokeMethod);
      return td;
    }

    public static TypeDefinition CreateDelegateFromMethod(ModuleDefinition module, string @namespace, string name,
      MethodDefinition method, TypeAttributes delegateVisibility = TypeAttributes.NotPublic)
    {
      TypeDefinition td = CreateDelegate(module, @namespace, name, delegateVisibility, method.MethodReturnType, method.Parameters);
      if (method.IsPInvokeImpl)
      {
        CustomAttribute ufpAttr = CreateUnmanagedFunctionPointerAttributeFromMethod(module, method);
        td.CustomAttributes.Add(ufpAttr);
      }
      return td;
    }

    public static CustomAttribute CreateUnmanagedFunctionPointerAttributeFromMethod(ModuleDefinition module, MethodDefinition method)
    {
      TypeReference ufpAttrRef = module.Import(typeof(UnmanagedFunctionPointerAttribute));
      TypeReference cconvRef = module.Import(typeof(CallingConvention));
      MethodReference ufpAttrCtor = ImportInstanceMethodRef(module, ufpAttrRef, ".ctor", null, cconvRef);
      CustomAttribute ufpAttr = new CustomAttribute(ufpAttrCtor);
      ufpAttr.ConstructorArguments.Add(new CustomAttributeArgument(cconvRef, GetPInvokeCConv(method)));
      CharSet charSet = GetPInvokeCharSet(method);
      if (charSet != CharSet.None)
      {
        TypeReference charSetRef = module.Import(typeof(CharSet));
        ufpAttr.Fields.Add(new CustomAttributeNamedArgument("CharSet", new CustomAttributeArgument(charSetRef, charSet)));
      }
      if (!GetPInvokeBestFitMapping(method)) //default true
      {
        ufpAttr.Fields.Add(new CustomAttributeNamedArgument("BestFitMapping", 
          new CustomAttributeArgument(module.TypeSystem.Boolean, false)));
      }
      if (GetPInvokeThrowOnUnmappableChar(method)) //default false
      {
        ufpAttr.Fields.Add(new CustomAttributeNamedArgument("ThrowOnUnmappableChar",
          new CustomAttributeArgument(module.TypeSystem.Boolean, true)));
      }
      if (GetPInvokeSetLastError(method)) //default false
      {
        ufpAttr.Fields.Add(new CustomAttributeNamedArgument("SetLastError",
          new CustomAttributeArgument(module.TypeSystem.Boolean, true)));
      }
      return ufpAttr;
    }

    public static bool GetPInvokeSetLastError(MethodDefinition method)
    {
      if (!method.HasPInvokeInfo)
        return false;
      return method.PInvokeInfo.SupportsLastError;
    }

    public static bool GetPInvokeThrowOnUnmappableChar(MethodDefinition method)
    {
      if (!method.HasPInvokeInfo)
        return false;
      return method.PInvokeInfo.IsThrowOnUnmappableCharEnabled;
    }

    public static bool GetPInvokeBestFitMapping(MethodDefinition method)
    {
      if (!method.HasPInvokeInfo)
        return true;
      return method.PInvokeInfo.IsBestFitEnabled;
    }

    public static CharSet GetPInvokeCharSet(MethodDefinition method)
    {
      if (!method.HasPInvokeInfo)
        return CharSet.None;
      if (method.PInvokeInfo.IsCharSetAnsi)
        return CharSet.Ansi;
      else if (method.PInvokeInfo.IsCharSetAuto)
        return CharSet.Auto;
      else if (method.PInvokeInfo.IsCharSetUnicode)
        return CharSet.Unicode;
      else
        return CharSet.None;
    }

    public static CallingConvention GetPInvokeCConv(MethodDefinition method)
    {
      if (method.HasPInvokeInfo)
      {
        PInvokeInfo pii = method.PInvokeInfo;
        if (pii.IsCallConvCdecl)
          return CallingConvention.Cdecl;
        else if (pii.IsCallConvFastcall)
          return CallingConvention.FastCall;
        else if (pii.IsCallConvStdCall)
          return CallingConvention.StdCall;
        else if (pii.IsCallConvThiscall)
          return CallingConvention.ThisCall;
        else if (pii.IsCallConvWinapi)
          return CallingConvention.Winapi;
      }
      return CecilCConvToInteropCConv(method.CallingConvention);
    }

    public static CallingConvention CecilCConvToInteropCConv(MethodCallingConvention methodCallingConvention)
    {
      switch (methodCallingConvention)
      {
        case MethodCallingConvention.C:
          return CallingConvention.Cdecl;
        case MethodCallingConvention.FastCall:
          return CallingConvention.FastCall;
        case MethodCallingConvention.StdCall:
          return CallingConvention.StdCall;
        case MethodCallingConvention.ThisCall:
          return CallingConvention.ThisCall;
        default:
          return CallingConvention.Winapi;
      }
    }

    public static Instruction Clone(this Instruction src)
    {
      OpCode oc = src.OpCode;
      object op = src.Operand;
      if (op == null)
        return Instruction.Create(oc);
      if (op is TypeReference)
        return Instruction.Create(oc, (TypeReference)op);
      if (op is CallSite)
        return Instruction.Create(oc, (CallSite)op);
      if (op is MethodReference)
        return Instruction.Create(oc, (MethodReference)op);
      if (op is FieldReference)
        return Instruction.Create(oc, (FieldReference)op);
      if (op is string)
        return Instruction.Create(oc, (string)op);
      if (op is sbyte)
        return Instruction.Create(oc, (sbyte)op);
      if (op is byte)
        return Instruction.Create(oc, (byte)op);
      if (op is int)
        return Instruction.Create(oc, (int)op);
      if (op is long)
        return Instruction.Create(oc, (long)op);
      if (op is float)
        return Instruction.Create(oc, (float)op);
      if (op is double)
        return Instruction.Create(oc, (double)op);
      if (op is Instruction)
        return Instruction.Create(oc, (Instruction)op);
      if (op is Instruction[])
        return Instruction.Create(oc, (Instruction[])op);
      if (op is VariableDefinition)
        return Instruction.Create(oc, (VariableDefinition)op);
      if (op is ParameterDefinition)
        return Instruction.Create(oc, (ParameterDefinition)op);
      throw new NotSupportedException(String.Format("Operand type '{0}' unsupported.", src.Operand.GetType().AssemblyQualifiedName));
    }

    public static Instruction ShortestStloc(int varIdx)
    {
      switch (varIdx)
      {
        case 0: return Instruction.Create(OpCodes.Stloc_0);
        case 1: return Instruction.Create(OpCodes.Stloc_1);
        case 2: return Instruction.Create(OpCodes.Stloc_2);
        case 3: return Instruction.Create(OpCodes.Stloc_3);
        default:
          return varIdx <= 255 ? 
            Instruction.Create(OpCodes.Stloc_S, (byte)varIdx) : 
            Instruction.Create(OpCodes.Stloc, varIdx);
      }
    }

    public static Instruction ShortestLdloc(int varIdx)
    {
      switch (varIdx)
      {
        case 0: return Instruction.Create(OpCodes.Ldloc_0);
        case 1: return Instruction.Create(OpCodes.Ldloc_1);
        case 2: return Instruction.Create(OpCodes.Ldloc_2);
        case 3: return Instruction.Create(OpCodes.Ldloc_3);
        default:
          return varIdx <= 255 ? 
            Instruction.Create(OpCodes.Ldloc_S, (byte)varIdx) : 
            Instruction.Create(OpCodes.Ldloc, varIdx);
      }
    }
    
    public static Instruction ShortestLdarg(ParameterDefinition p)
    {
      switch (p.Index)
      {
        case 0: return Instruction.Create(OpCodes.Ldarg_0);
        case 1: return Instruction.Create(OpCodes.Ldarg_1);
        case 2: return Instruction.Create(OpCodes.Ldarg_2);
        case 3: return Instruction.Create(OpCodes.Ldarg_3);
        default:
          return p.Index <= 255 ? 
            Instruction.Create(OpCodes.Ldarg_S, p) : 
            Instruction.Create(OpCodes.Ldarg, p);
      }
    }

    public static MethodReference CreateStaticMethodRef(TypeReference declaringType, string name, TypeReference returnType = null, params TypeReference[] parameters)
    {
      if (returnType == null)
        returnType = declaringType.Module.TypeSystem.Void;
      MethodReference ret = new MethodReference(name, returnType, declaringType);
      foreach (TypeReference p in parameters)
        ret.Parameters.Add(new ParameterDefinition(p));
      return ret;
    }

    public static MethodReference CreateInstanceMethodRef(TypeReference declaringType, string name, TypeReference returnType = null, params TypeReference[] parameters)
    {
      MethodReference ret = CreateStaticMethodRef(declaringType, name, returnType, parameters);
      ret.HasThis = true;
      return ret;
    }

    public static MethodReference ImportStaticMethodRef(ModuleDefinition module, TypeReference declaringType, string name, TypeReference returnType = null, params TypeReference[] parameters)
    {
      return module.Import(CreateStaticMethodRef(declaringType, name, returnType, parameters));
    }
    
    public static MethodReference ImportInstanceMethodRef(ModuleDefinition module, TypeReference declaringType, string name, TypeReference returnType = null, params TypeReference[] parameters)
    {
      return module.Import(CreateInstanceMethodRef(declaringType, name, returnType, parameters));
    }
    

  }
}
