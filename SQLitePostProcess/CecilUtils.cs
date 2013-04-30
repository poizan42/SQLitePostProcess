using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;
using System.Runtime.InteropServices;

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
        throw new ArgumentException("Parameter may only contain visibility attribute.", "delegateVisibility");
      var objectRef = module.TypeSystem.Object;
      var voidRef = module.TypeSystem.Void;
      var intPtrRef = module.TypeSystem.IntPtr;
      var multiDelegateRef = module.Import(typeof(MulticastDelegate));
      var asyncCallbackRef = module.Import(typeof(AsyncCallback));
      var iAsyncResultRef = module.Import(typeof(IAsyncResult));
      
      if (returnType == null)
        returnType = new MethodReturnType(null) { ReturnType = voidRef };
      if (parameters == null)
        parameters = Enumerable.Empty<ParameterDefinition>();

      TypeAttributes typeAttributes = delegateVisibility | TypeAttributes.AnsiClass | TypeAttributes.Sealed;
      TypeDefinition td = new TypeDefinition(@namespace, name, typeAttributes, multiDelegateRef);
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
      MethodReference ufpAttrCtor = ufpAttrRef.Resolve().Methods.First(m => 
        m.IsRuntimeSpecialName && m.Name == ".ctor" && m.Parameters.Count == 1 &&
        m.Parameters[0].ParameterType.FullName == typeof(CallingConvention).FullName);
      ufpAttrCtor = module.Import(ufpAttrCtor);
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
  }
}
