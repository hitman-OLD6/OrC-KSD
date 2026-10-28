using System.Reflection;
using System.Reflection.Emit;

namespace Orc.Core;

/// <summary>
/// 框架产物（代理类型）工厂：用 Reflection.Emit 生成「派生自作者视图类、实现 <see cref="IContextView"/>」的类型。
/// 每个声明的属性访问器被覆写为访问闸门：权限判定、数据读写、失效引用校验直接烘焙在 IL 中，
/// 且只引用公共面（Dictionary / Ref / 公共异常 / IContextView），不依赖库内 internal 成员。
/// 每次调用生成唯一类型名，避免并发与重复构建时的名称冲突。
/// </summary>
internal static class ViewProxyFactory
{
    private static readonly object EmitLock = new();
    private static readonly ModuleBuilder Module = CreateModule();

    private static readonly MethodInfo TryGetValueMethod =
        typeof(Dictionary<string, object?>).GetMethod(nameof(Dictionary<string, object?>.TryGetValue))!;

    private static readonly MethodInfo SetItemMethod =
        typeof(Dictionary<string, object?>).GetProperty("Item")!.GetSetMethod()!;

    private static readonly ConstructorInfo StaleCtor =
        typeof(StaleReferenceException).GetConstructor(new[] { typeof(string) })!;

    private static readonly ConstructorInfo PermissionDeniedCtor =
        typeof(PermissionDeniedException).GetConstructor(new[] { typeof(string) })!;

    private static readonly ConstructorInfo KeyNotFoundCtor =
        typeof(KeyNotFoundException).GetConstructor(new[] { typeof(string) })!;

    private static ModuleBuilder CreateModule()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Orc.Views.Dynamic"),
            AssemblyBuilderAccess.Run);
        return assembly.DefineDynamicModule("Orc.Views.Dynamic");
    }

    /// <summary>为指定视图类型创建框架产物类型。</summary>
    internal static Type CreateProxyType(Type viewType, IReadOnlyList<PropertyMeta> properties)
    {
        lock (EmitLock)
        {
            var typeBuilder = Module.DefineType(
                $"Orc.Views.{viewType.Name}__Proxy_{Guid.NewGuid():N}",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed
                    | TypeAttributes.AutoClass | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit,
                viewType,
                new[] { typeof(IContextView) });

            var dataField = typeBuilder.DefineField(
                "__orc_data", typeof(Dictionary<string, object?>), FieldAttributes.Private);
            var sealedField = typeBuilder.DefineField(
                "__orc_sealed", typeof(bool), FieldAttributes.Private);

            EmitConstructor(typeBuilder, viewType);
            EmitBind(typeBuilder, dataField);
            EmitSeal(typeBuilder, sealedField);

            foreach (var property in properties)
            {
                EmitPropertyAccessors(typeBuilder, dataField, property);
            }

            return typeBuilder.CreateType()!;
        }
    }

    private static void EmitConstructor(TypeBuilder typeBuilder, Type viewType)
    {
        var baseCtor = viewType.GetConstructor(Type.EmptyTypes)
            ?? throw new ArgumentException($"视图类型 '{viewType.FullName}' 需要 public 无参构造。", nameof(viewType));

        var ctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var il = ctorBuilder.GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitBind(TypeBuilder typeBuilder, FieldInfo dataField)
    {
        var bindBuilder = typeBuilder.DefineMethod(
            nameof(IContextView.Bind),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig
                | MethodAttributes.NewSlot | MethodAttributes.Final,
            typeof(void),
            new[] { typeof(Dictionary<string, object?>), typeof(Context) });

        var il = bindBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, dataField);
        il.Emit(OpCodes.Ret);

        typeBuilder.DefineMethodOverride(bindBuilder, typeof(IContextView).GetMethod(nameof(IContextView.Bind))!);
    }

    private static void EmitSeal(TypeBuilder typeBuilder, FieldInfo sealedField)
    {
        var sealBuilder = typeBuilder.DefineMethod(
            nameof(IContextView.Seal),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig
                | MethodAttributes.NewSlot | MethodAttributes.Final,
            typeof(void),
            Type.EmptyTypes);

        var il = sealBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, sealedField);
        il.Emit(OpCodes.Ret);

        typeBuilder.DefineMethodOverride(sealBuilder, typeof(IContextView).GetMethod(nameof(IContextView.Seal))!);
    }

    private static void EmitPropertyAccessors(TypeBuilder typeBuilder, FieldInfo dataField, PropertyMeta property)
    {
        var propertyType = property.Property.PropertyType;
        var getter = property.Property.GetGetMethod();
        var setter = property.Property.GetSetMethod();

        if (getter is not null)
        {
            var getBuilder = typeBuilder.DefineMethod(
                $"get_{property.Name}",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName,
                propertyType,
                Type.EmptyTypes);

            EmitGetterBody(getBuilder.GetILGenerator(), dataField, property);
            typeBuilder.DefineMethodOverride(getBuilder, getter);
        }

        if (setter is not null)
        {
            var setBuilder = typeBuilder.DefineMethod(
                $"set_{property.Name}",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName,
                typeof(void),
                new[] { propertyType });

            EmitSetterBody(setBuilder.GetILGenerator(), dataField, property);
            typeBuilder.DefineMethodOverride(setBuilder, setter);
        }
    }

    private static void EmitGetterBody(ILGenerator il, FieldInfo dataField, PropertyMeta property)
    {
        var propertyType = property.Property.PropertyType;
        var display = $"{property.Property.DeclaringType!.Name}.{property.Name}";

        if (property.Access == ViewPropertyAccess.None)
        {
            // 无有效访问特性：任何读取一律拒绝（权限矩阵闸门）。
            EmitThrow(il, PermissionDeniedCtor, $"视图属性 '{display}' 未声明 [Read]/[Mutate]，禁止读取。");
            return;
        }

        var valueLocal = il.DeclareLocal(typeof(object));
        var missing = il.DefineLabel();
        var found = il.DefineLabel();

        // var value = __orc_data[name];
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, dataField);
        il.Emit(OpCodes.Ldstr, property.Name);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, TryGetValueMethod);
        il.Emit(OpCodes.Brfalse, missing);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Brfalse, missing); // 键存在但值为 null 视为缺失
        il.Emit(OpCodes.Br, found);

        // 缺失分支：缺省回退（default）或防御性报错。
        il.MarkLabel(missing);
        if (property.Optional)
        {
            var defaultLocal = il.DeclareLocal(propertyType);
            il.Emit(OpCodes.Ldloca, defaultLocal);
            il.Emit(OpCodes.Initobj, propertyType);
            il.Emit(OpCodes.Ldloc, defaultLocal);
            il.Emit(OpCodes.Ret);
        }
        else
        {
            EmitThrow(il, KeyNotFoundCtor, $"视图属性 '{display}' 的必填数据缺失。");
        }

        // 命中分支：Ref 类型先做失效校验，再返回值。
        il.MarkLabel(found);
        if (property.IsRefType)
        {
            var alive = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, valueLocal);
            il.Emit(OpCodes.Castclass, propertyType);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Callvirt, propertyType.GetProperty(nameof(Ref<object>.IsAlive))!.GetGetMethod()!);
            il.Emit(OpCodes.Brtrue, alive);
            il.Emit(OpCodes.Pop);
            EmitThrow(il, StaleCtor, $"视图属性 '{display}' 的引用已失效。");
            il.MarkLabel(alive);
            il.Emit(OpCodes.Ret);
        }
        else
        {
            il.Emit(OpCodes.Ldloc, valueLocal);
            il.Emit(OpCodes.Unbox_Any, propertyType);
            il.Emit(OpCodes.Ret);
        }
    }

    private static void EmitSetterBody(ILGenerator il, FieldInfo dataField, PropertyMeta property)
    {
        var propertyType = property.Property.PropertyType;
        var display = $"{property.Property.DeclaringType!.Name}.{property.Name}";

        if (property.Access != ViewPropertyAccess.Mutate)
        {
            // 仅 [Mutate] 可写；[Read] 与无特性一律拒绝（权限矩阵闸门）。
            // 消息要点明"只读"：作者看到的不是"缺一个特性"，而是"这个字段本来就只读，写它属于越权"。
            EmitThrow(il, PermissionDeniedCtor, $"视图属性 '{display}' 是只读的（未声明 [Mutate]），禁止写入。");
            return;
        }

        // __orc_data[name] = value;（可增改：允许创建新键）
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, dataField);
        il.Emit(OpCodes.Ldstr, property.Name);
        il.Emit(OpCodes.Ldarg_1);
        if (propertyType.IsValueType)
        {
            il.Emit(OpCodes.Box, propertyType);
        }

        il.Emit(OpCodes.Callvirt, SetItemMethod);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitThrow(ILGenerator il, ConstructorInfo exceptionCtor, string message)
    {
        il.Emit(OpCodes.Ldstr, message);
        il.Emit(OpCodes.Newobj, exceptionCtor);
        il.Emit(OpCodes.Throw);
    }
}
