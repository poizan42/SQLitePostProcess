using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;

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
    }

    private static void FindAndReplaceNativeMethods(AssemblyDefinition assembly)
    {
      foreach (var module in assembly.Modules)
      {
        foreach (var type in module.Types)
        {
          if (type.FullName == "System.Data.SQLite.UnsafeNativeMethods")
          {
            ReplaceNativeMethods(type);
            return;
          }
        }
      }
    }

    private static void ReplaceNativeMethods(TypeDefinition type)
    {
      /*foreach (var method in type.Methods.Where(m => m.IsPInvokeImpl && m.PInvokeInfo.Module != null &&
        m.PInvokeInfo.Module.Name == SQLITE_DLL))
      {
        TypeDefinition methodDelegateType = 
        type.Fields.Add()
      }*/
    }
  }
}
