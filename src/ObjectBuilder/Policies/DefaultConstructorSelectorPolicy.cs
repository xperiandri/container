﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Attributes;
using Unity.Builder;
using Unity.Builder.Selection;
using Unity.Policy;

namespace Unity.ObjectBuilder.Policies
{
    /// <summary>
    /// An implementation of <see cref="IConstructorSelectorPolicy"/> that is
    /// aware of the build keys used by the Unity container.
    /// </summary>
    public class DefaultConstructorSelectorPolicy : IConstructorSelectorPolicy
    {
        private DefaultParameterResolverPolicy _factory;

        public DefaultConstructorSelectorPolicy(DefaultParameterResolverPolicy factory = null)
        {
            _factory = factory ?? new DefaultParameterResolverPolicy();
            Markers = new List<Type> { typeof(InjectionConstructorAttribute) };
        }


        public IList<Type> Markers { get; }


        public SelectedConstructor SelectConstructor(IBuilderContext context, IPolicyList resolverPolicyDestination)
        {
            Type typeToConstruct = context.BuildKey.Type;
            var constructors = typeToConstruct.GetTypeInfo()
                                              .DeclaredConstructors
                                              .Where(c => c.IsStatic == false && c.IsPublic)
                                              .ToArray();

            var ctor = 1 == constructors.Length ? constructors[0]
                                                : SelectAttributedConstructor(constructors) ??
                                                  SelectInjectionConstructor(constructors, context);
            if (ctor != null)
            {
                return CreateSelectedConstructor(ctor);
            }

            return null;
        }

        private ConstructorInfo SelectAttributedConstructor(IEnumerable<ConstructorInfo> constructors)
        {
            return (from constructor in constructors
                    from type in Markers
                    where constructor.IsDefined(type, true)
                    select constructor)
                .FirstOrDefault();
        }


        private static ConstructorInfo SelectInjectionConstructor(ConstructorInfo[] constructors, IBuilderContext context)
        {
            Array.Sort(constructors, (a, b) =>
            {
                var qtd = b.GetParameters().Length.CompareTo(a.GetParameters().Length);
                if (qtd == 0)
                {
                    return b.GetParameters().Sum(p => p.ParameterType.GetTypeInfo().IsInterface ? 1 : 0)
                        .CompareTo(a.GetParameters().Sum(p => p.ParameterType.GetTypeInfo().IsInterface ? 1 : 0));
                }
                return qtd;
            });

            ConstructorInfo bestConstructor = null;
            HashSet<Type> bestConstructorParameterTypes = null;

            foreach (var ctorInfo in constructors)
            {
                var parameters = ctorInfo.GetParameters();
#if NET40
                if (parameters.All(p => CanResolve(context.Container, p.ParameterType) || null != p.DefaultValue && !(p.DefaultValue is DBNull)))
#else
                if (parameters.All(p => CanResolve(context.Container, p.ParameterType) || p.HasDefaultValue))
#endif
                {
                    if (bestConstructor == null)
                    {
                        bestConstructor = ctorInfo;
                    }
                    else
                    {
                        // Since we're visiting constructors in decreasing order of number of parameters,
                        // we'll only see ambiguities or supersets once we've seen a 'bestConstructor'.

                        if (bestConstructorParameterTypes == null)
                        {
                            bestConstructorParameterTypes = new HashSet<Type>(
                                bestConstructor.GetParameters().Select(p => p.ParameterType));
                        }

                        if (!bestConstructorParameterTypes.IsSupersetOf(parameters.Select(p => p.ParameterType)))
                        {
                            if (bestConstructorParameterTypes.All(p => p.GetTypeInfo().IsInterface)
                                && !parameters.All(p => p.ParameterType.GetTypeInfo().IsInterface))
                                return bestConstructor;

                            throw new InvalidOperationException($"Failed to select a constructor for {context.BuildKey.Type.FullName}");
                        }

                        return bestConstructor;
                    }
                }
            }

            if (bestConstructor == null)
            {
                //return null;
                throw new InvalidOperationException(
                    $"Builder not found for { context.BuildKey.Type.FullName}");
            }

            return bestConstructor;
        }

        internal static bool CanResolve(IUnityContainer container, Type type)
        {
            var info = type.GetTypeInfo();

            if (info.IsClass && !info.IsAbstract)
            {
                if (typeof(Delegate).GetTypeInfo().IsAssignableFrom(info) || typeof(string) == type || info.IsEnum
                    || type.IsArray || info.IsPrimitive)
                {
                    return container.IsRegistered(type);
                }

                return true;
            }

            if (info.IsGenericType)
            {
                var genericType = type.GetGenericTypeDefinition();

                if (genericType == typeof(IEnumerable<>) ||
                    container.IsRegistered(genericType))
                {
                    return true;
                }
            }

            return container.IsRegistered(type);
        }

        private SelectedConstructor CreateSelectedConstructor(ConstructorInfo ctor)
        {
            var result = new SelectedConstructor(ctor);

            foreach (ParameterInfo param in ctor.GetParameters())
            {
                result.AddParameterResolver(_factory.CreateResolver(param));
            }

            return result;
        }
    }
}