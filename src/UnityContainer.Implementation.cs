﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Unity.Aspect.Build;
using Unity.Aspect.Generic;
using Unity.Aspect.Select;
using Unity.Build.Context;
using Unity.Build.Pipeline;
using Unity.Build.Policy;
using Unity.Build.Stage;
using Unity.Builder;
using Unity.Container.Lifetime;
using Unity.Container.Pipeline;
using Unity.Container.Registration;
using Unity.Container.Storage;
using Unity.Events;
using Unity.Extension;
using Unity.Policy;
using Unity.Registration;
using Unity.Storage;

namespace Unity
{
#if DEBUG
    [DebuggerDisplay("Unity: {Id}  Registrations: {System.Linq.Enumerable.Count(Registrations)}")]
#endif
    [CLSCompliant(true)]
    public partial class UnityContainer
    {
        #region Delegates

        private delegate IRegistry<string, IPolicySet> GetTypeDelegate(Type type);
        private delegate object GetPolicyDelegate(Type type, string name, Type requestedType);
        private delegate IPolicySet RegisterDelegate(ExplicitRegistration registration);

        public delegate IPolicySet GetRegistrationDelegate(Type type, string name);
        internal delegate IBuilderPolicy GetPolicyListDelegate(Type type, string name, Type policyInterface, out IPolicyList list);
        internal delegate void SetPolicyDelegate(Type type, string name, Type policyInterface, IBuilderPolicy policy);
        internal delegate void ClearPolicyDelegate(Type type, string name, Type policyInterface);

        internal delegate TPipeline BuildPlan<out TPipeline>(IUnityContainer container, IPolicySet set, Factory<Type, ResolveMethod> factory = null);

        #endregion


        #region Fields
      
#if DEBUG
        public readonly string Id;
#endif

        // Container specific
        private readonly UnityContainer _root;
        private readonly UnityContainer _parent;
        internal readonly LifetimeContainer _lifetimeContainer;
        private List<UnityContainerExtension> _extensions;

        ///////////////////////////////////////////////////////////////////////
        // Factories

        private readonly StagedFactoryChain<Registration<ITypeFactory<Type>>, RegisterStage> _genericRegistrationFactories;
        private readonly StagedFactoryChain<Registration<ResolveMethod>, RegisterStage> _implicitRegistrationFactories;
        private readonly StagedFactoryChain<Registration<ResolveMethod>, RegisterStage> _explicitRegistrationFactories;
        private readonly StagedFactoryChain<Registration<ResolveMethod>, RegisterStage> _instanceRegistrationFactories;

        private readonly StagedFactoryChain<Factory<Type, InjectionConstructor>, SelectMemberStage> _selectConstructorFactories;
        private readonly StagedFactoryChain<Factory<Type, IEnumerable<InjectionMember>>,  SelectMemberStage> _injectionMembersFactories;

        ///////////////////////////////////////////////////////////////////////
        // Pipelines

        // Registration
        private Registration<ITypeFactory<Type>> _genericRegistrationPipeline;
        private Registration<ResolveMethod> _explicitRegistrationPipeline;
        private Registration<ResolveMethod> _implicitRegistrationPipeline;
        private Registration<ResolveMethod> _instanceRegistrationPipeline;

        // Member Selection
        private Factory<Type, InjectionConstructor> _constructorSelectionPipeline;
        private Factory<Type, IEnumerable<InjectionMember>> _injectionMembersPipeline;

        private GetRegistrationDelegate _getRegistration;

        ///////////////////////

        // Policies
        private readonly ContainerExtensionContext _extensionContext;

        // Registrations
        private readonly object _syncRoot = new object();
        private HashRegistry<Type, IRegistry<string, IPolicySet>> _registrations;

        // Events
#pragma warning disable 67
        private event EventHandler<RegisterEventArgs> Registering;
        private event EventHandler<RegisterInstanceEventArgs> RegisteringInstance;
#pragma warning restore 67
        private event EventHandler<ChildContainerCreatedEventArgs> ChildContainerCreated;

        // Methods
        internal Func<Type, string, bool> IsTypeRegistered;
        internal Func<Type, string, ImplicitRegistration> GetRegistration;
        internal Func<IBuilderContext, object> BuilUpPipeline;
        internal GetPolicyListDelegate GetPolicyList;
        internal SetPolicyDelegate SetPolicy;
        internal ClearPolicyDelegate ClearPolicy;

        private GetPolicyDelegate _getPolicy;
        private GetTypeDelegate _getType;
        private RegisterDelegate _register;

        #endregion


        #region Constructors

        /// <summary>
        /// Create a default <see cref="UnityContainer"/>.
        /// </summary>
        public UnityContainer()
        {
            #if DEBUG
            Id = "Root";
            #endif
            ///////////////////////////////////////////////////////////////////////
            // Root container
            _root = this;
            _lifetimeContainer = new LifetimeContainer(this);
            _registrations = new HashRegistry<Type, IRegistry<string, IPolicySet>>(ContainerInitialCapacity)
                { [null] = null };

            ///////////////////////////////////////////////////////////////////////
            // Factories

            _genericRegistrationFactories = new StagedFactoryChain<Registration<ITypeFactory<Type>>, RegisterStage>
            {
                { GenericInjectionAspect.InjectionFactoryAspectFactory, RegisterStage.Injection },
                {   GenericMappingAspect.MappingAspectFactory,          RegisterStage.TypeMapping },
                {   GenericFactoryAspect.ResolveFactoryAspectFactory,   RegisterStage.Creation }
            };
            _implicitRegistrationFactories = new StagedFactoryChain<Registration<ResolveMethod>, RegisterStage>
            {
                {BuildLifetimeAspect.ImplicitRegistrationLifetimeAspectFactory, RegisterStage.Lifetime},
                { BuildMappingAspect.ImplicitRegistrationMappingAspectFactory,  RegisterStage.TypeMapping},
                {                    BuildImplicitRegistrationAspectFactory,    RegisterStage.Creation},
            };
            _explicitRegistrationFactories = new StagedFactoryChain<Registration<ResolveMethod>, RegisterStage>
            {
                {BuildLifetimeAspect.ExplicitRegistrationLifetimeAspectFactory, RegisterStage.Lifetime},
                { BuildMappingAspect.ExplicitRegistrationMappingAspectFactory,  RegisterStage.TypeMapping},
                {                    BuildExplicitRegistrationAspectFactory,    RegisterStage.Creation},
            };
            _instanceRegistrationFactories = new StagedFactoryChain<Registration<ResolveMethod>, RegisterStage>
            {
                { BuildLifetimeAspect.ExplicitRegistrationLifetimeAspectFactory, RegisterStage.Lifetime},
            };

            _selectConstructorFactories = new StagedFactoryChain<Factory<Type, InjectionConstructor>, SelectMemberStage>
            {
                { SelectAttributedMembers.SelectConstructorPipelineFactory, SelectMemberStage.Attrubute  },
                {SelectLongestConstructor.SelectConstructorPipelineFactory, SelectMemberStage.Reflection },
            };

            _injectionMembersFactories = new StagedFactoryChain<Factory<Type, IEnumerable<InjectionMember>>, SelectMemberStage>
            {
                { SelectAttributedMembers.SelectPropertiesPipelineFactory, SelectMemberStage.Attrubute },
                {SelectAttributedMembers.SelectMethodsPipelineFactory,     SelectMemberStage.Reflection},
            };

            ///////////////////////////////////////////////////////////////////////
            // Create Pipelines

            _genericRegistrationPipeline  = _genericRegistrationFactories.BuildPipeline();
            _explicitRegistrationPipeline = _explicitRegistrationFactories.BuildPipeline();
            _instanceRegistrationPipeline = _instanceRegistrationFactories.BuildPipeline();
            _implicitRegistrationPipeline = _implicitRegistrationFactories.BuildPipeline();

            _constructorSelectionPipeline = _selectConstructorFactories.BuildPipeline();
            _injectionMembersPipeline     = _injectionMembersFactories.BuildPipeline();

            _getRegistration = GetOrAdd;

            // Context and policies
            _extensionContext = new ContainerExtensionContext(this);

            // Methods
            _getType = Get;
            _getPolicy = Get;
            _register = AddOrUpdate;

            BuilUpPipeline = ThrowingBuildUp;
            IsTypeRegistered = (type, name) => null != Get(type, name);
            GetRegistration = GetOrAdd;
            GetPolicyList = Get;
            SetPolicy = Set;
            ClearPolicy = Clear;

            // Default Policies
            //Set( null, null, GetDefaultPolicies()); 
            //Set(typeof(Func<>), string.Empty, typeof(ILifetimePolicy), new PerResolveLifetimeManager());
            //Set(typeof(Func<>), string.Empty, typeof(IBuildPlanPolicy), new DeferredResolveCreatorPolicy());
            //Set(typeof(Lazy<>), string.Empty, typeof(IBuildPlanCreatorPolicy), new GenericLazyBuildPlanCreatorPolicy());

            // Register this instance
            RegisterInstance(typeof(IUnityContainer), null, this, new ContainerLifetimeManager());
        }

        /// <summary>
        /// Create a <see cref="Unity.UnityContainer"/> with the given parent container.
        /// </summary>
        /// <param name="parent">The parent <see cref="Unity.UnityContainer"/>. The current object
        /// will apply its own settings first, and then check the parent for additional ones.</param>
        private UnityContainer(UnityContainer parent)
        {
            #if DEBUG
            Id = $"{parent.Id}-*";
            #endif
            ///////////////////////////////////////////////////////////////////////
            // Child container initialization

            _lifetimeContainer = new LifetimeContainer(this);
            _extensionContext = new ContainerExtensionContext(this);

            ///////////////////////////////////////////////////////////////////////
            // Parent
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _parent._lifetimeContainer.Add(this);
            _root = _parent._root;


            ///////////////////////////////////////////////////////////////////////
            // Factories

            // TODO: Create on demand
            _genericRegistrationFactories = new StagedFactoryChain<Registration<ITypeFactory<Type>>, RegisterStage>(_parent._genericRegistrationFactories);
            _implicitRegistrationFactories = new StagedFactoryChain<Registration<ResolveMethod>, RegisterStage>(_parent._implicitRegistrationFactories);
            _explicitRegistrationFactories = new StagedFactoryChain<Registration<ResolveMethod>, RegisterStage>(_parent._explicitRegistrationFactories);
            _instanceRegistrationFactories = new StagedFactoryChain<Registration<ResolveMethod>, RegisterStage>(_parent._instanceRegistrationFactories);
            _selectConstructorFactories = new StagedFactoryChain<Factory<Type, InjectionConstructor>, SelectMemberStage>(_parent._selectConstructorFactories);
            _injectionMembersFactories =  new StagedFactoryChain<Factory<Type, IEnumerable<InjectionMember>>, SelectMemberStage>(_parent._injectionMembersFactories);

            ///////////////////////////////////////////////////////////////////////
            // Register disposable factory chains

            // TODO: Create on demand
            _lifetimeContainer.Add(_implicitRegistrationFactories);
            _lifetimeContainer.Add(_explicitRegistrationFactories);
            _lifetimeContainer.Add(_instanceRegistrationFactories);
            _lifetimeContainer.Add(_selectConstructorFactories);
            _lifetimeContainer.Add(_injectionMembersFactories);

            ///////////////////////////////////////////////////////////////////////
            // Create Pipelines

            // TODO: Create on demand
            _implicitRegistrationPipeline  = _parent._implicitRegistrationPipeline;
            _explicitRegistrationPipeline = _parent._explicitRegistrationPipeline;
            _instanceRegistrationPipeline = _parent._instanceRegistrationPipeline;
            _constructorSelectionPipeline = _parent._constructorSelectionPipeline;
            _injectionMembersPipeline     = _parent._injectionMembersPipeline;


            // Methods
            _getPolicy = _parent._getPolicy;
            _register  = CreateAndSetOrUpdate;

            BuilUpPipeline = _parent.BuilUpPipeline;
            IsTypeRegistered = _parent.IsTypeRegistered;
            GetRegistration = _parent.GetRegistration;
            GetPolicyList = parent.GetPolicyList;
            SetPolicy = CreateAndSetPolicy;
            ClearPolicy = delegate { };
        }

        #endregion


        #region Defaults

        //private IPolicySet GetDefaultPolicies()
        //{
        //    var defaults = new ImplicitRegistration(null, null);

        //    defaults.Set(typeof(IBuildPlanCreatorPolicy), new DynamicMethodBuildPlanCreatorPolicy(_buildPlanStrategies));
        //    defaults.Set(typeof(IConstructorSelectorPolicy), new DefaultUnityConstructorSelectorPolicy());
        //    defaults.Set(typeof(IPropertySelectorPolicy), new DefaultUnityPropertySelectorPolicy());
        //    defaults.Set(typeof(IMethodSelectorPolicy), new DefaultUnityMethodSelectorPolicy());

        //    return defaults;
        //}

        #endregion


        #region Implementation

        private void CreateAndSetPolicy(Type type, string name, Type policyInterface, IBuilderPolicy policy)
        {
            lock (GetRegistration)
            {
                if (null == _registrations)
                    SetupChildContainerBehaviors();
            }

            Set(type, name, policyInterface, policy);
        }

        private IPolicySet CreateAndSetOrUpdate(ExplicitRegistration registration)
        {
            lock (GetRegistration)
            {
                if (null == _registrations)
                    SetupChildContainerBehaviors();
            }

            return AddOrUpdate(registration);
        }

        private void SetupChildContainerBehaviors()
        {
            _registrations = new HashRegistry<Type, IRegistry<string, IPolicySet>>(ContainerInitialCapacity);
            _getPolicy = Get;
            _register = AddOrUpdate;

            IsTypeRegistered = IsTypeRegisteredLocally;
            GetRegistration = (type, name) => (ImplicitRegistration)Get(type, name) ?? _parent.GetRegistration(type, name);
            GetPolicyList = Get;
            SetPolicy = Set;
            ClearPolicy = Clear;
        }

        private static object ThrowingBuildUp(IBuilderContext context)
        {

            return context.Existing;
        }

        private static MiniHashSet<ImplicitRegistration> GetNotEmptyRegistrations(UnityContainer container, Type type)
        {
            MiniHashSet<ImplicitRegistration> set;

            if (null != container._parent)
                set = GetNotEmptyRegistrations(container._parent, type);
            else
                set = new MiniHashSet<ImplicitRegistration>();

            if (null == container._registrations) return set;

            var registrations = container.Get(type);
            if (null != registrations && null != registrations.Values)
            {
                var registry = registrations.Values;
                foreach (var entry in registry)
                {
                    if (entry is IContainerRegistration registration && string.Empty != registration.Name)
                        set.Add((ImplicitRegistration)registration);
                }
            }

            var generic = type.GetTypeInfo().IsGenericType ? type.GetGenericTypeDefinition() : type;

            if (generic != type)
            {
                registrations = container.Get(generic);
                if (null != registrations && null != registrations.Values)
                {
                    var registry = registrations.Values;
                    foreach (var entry in registry)
                    {
                        if (entry is IContainerRegistration registration && string.Empty != registration.Name)
                            set.Add((ImplicitRegistration)registration);
                    }
                }
            }

            return set;
        }

        #endregion


        #region IDisposable Implementation

        /// <summary>
        /// Dispose this container instance.
        /// </summary>
        /// <remarks>
        /// This class doesn't have a finalizer, so <paramref name="disposing"/> will always be true.</remarks>
        /// <param name="disposing">True if being called registeredType the IDisposable.Dispose
        /// method, false if being called registeredType a finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            List<Exception> exceptions = null;

            try
            {
                _parent?._lifetimeContainer.Remove(this);
                _lifetimeContainer.Dispose();
            }
            catch (Exception e)
            {
                if (null == exceptions) exceptions = new List<Exception>();
                exceptions.Add(e);
            }

            if (null != _extensions)
            {
                foreach (IDisposable disposable in _extensions.OfType<IDisposable>()
                                                              .ToList())
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception e)
                    {
                        if (null == exceptions) exceptions = new List<Exception>();
                        exceptions.Add(e);
                    }
                }

                _extensions = null;
            }

            _registrations = new HashRegistry<Type, IRegistry<string, IPolicySet>>(1);

            if (null != exceptions && exceptions.Count == 1)
            {
                throw exceptions[0];
            }

            if (null != exceptions && exceptions.Count > 1)
            {
                throw new AggregateException(exceptions);
            }
        }

        private static ParentDelegate GetContextFactoryMethod()
        {
            var enclosure = new ResolutionContext[1];
            return () => ref enclosure[0];
        }

        #endregion
    }
}
