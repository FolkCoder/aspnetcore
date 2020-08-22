// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace Microsoft.AspNetCore.Components.Reflection
{
    internal static class ComponentProperties
    {
        private const BindingFlags _bindablePropertyFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

        // Right now it's not possible for a component to define a Parameter and a Cascading Parameter with
        // the same name. We don't give you a way to express this in code (would create duplicate properties),
        // and we don't have the ability to represent it in our data structures.
        private readonly static ConcurrentDictionary<Type, WritersForType> _cachedWritersByType
            = new ConcurrentDictionary<Type, WritersForType>();

        public static void SetProperties(in ParameterView parameters, object target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var targetType = target.GetType();
            if (!_cachedWritersByType.TryGetValue(targetType, out var writers))
            {
                writers = new WritersForType(targetType);
                _cachedWritersByType[targetType] = writers;
            }

            var requiredParametersWritten = 0u;
            // The logic is split up for simplicity now that we have CaptureUnmatchedValues parameters.
            if (writers.CaptureUnmatchedValuesWriter == null)
            {
                // Logic for components without a CaptureUnmatchedValues parameter
                foreach (var parameter in parameters)
                {
                    var parameterName = parameter.Name;
                    if (!writers.TryGetValue(parameterName, out var writer))
                    {
                        // Case 1: There is nowhere to put this value.
                        ThrowForUnknownIncomingParameterName(targetType, parameterName);
                        throw null; // Unreachable
                    }
                    else if (writer.Cascading && !parameter.Cascading)
                    {
                        // We don't allow you to set a cascading parameter with a non-cascading value. Put another way:
                        // cascading parameters are not part of the public API of a component, so it's not reasonable
                        // for someone to set it directly.
                        //
                        // If we find a strong reason for this to work in the future we can reverse our decision since
                        // this throws today.
                        ThrowForSettingCascadingParameterWithNonCascadingValue(targetType, parameterName);
                        throw null; // Unreachable
                    }
                    else if (!writer.Cascading && parameter.Cascading)
                    {
                        // We're giving a more specific error here because trying to set a non-cascading parameter
                        // with a cascading value is likely deliberate (but not supported), or is a bug in our code.
                        ThrowForSettingParameterWithCascadingValue(targetType, parameterName);
                        throw null; // Unreachable
                    }

                    SetProperty(target, writer, parameterName, parameter.Value, ref requiredParametersWritten);
                }
            }
            else
            {
                // Logic with components with a CaptureUnmatchedValues parameter
                var isCaptureUnmatchedValuesParameterSetExplicitly = false;
                Dictionary<string, object>? unmatched = null;
                foreach (var parameter in parameters)
                {
                    var parameterName = parameter.Name;
                    if (string.Equals(parameterName, writers.CaptureUnmatchedValuesPropertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        isCaptureUnmatchedValuesParameterSetExplicitly = true;
                    }

                    if (writers.TryGetValue(parameterName, out var writer))
                    {
                        if (!writer.Cascading && parameter.Cascading)
                        {
                            // Don't allow an "extra" cascading value to be collected - or don't allow a non-cascading
                            // parameter to be set with a cascading value.
                            //
                            // This is likely a bug in our infrastructure or an attempt to deliberately do something unsupported.
                            ThrowForSettingParameterWithCascadingValue(targetType, parameterName);
                            throw null; // Unreachable
                        }
                        else if (writer.Cascading && !parameter.Cascading)
                        {
                            // Allow unmatched parameters to collide with the names of cascading parameters. This is
                            // valid because cascading parameter names are not part of the public API. There's no
                            // way for the user of a component to know what the names of cascading parameters
                            // are.
                            unmatched ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                            unmatched[parameterName] = parameter.Value;
                        }
                        else
                        {
                            SetProperty(target, writer, parameterName, parameter.Value, ref requiredParametersWritten);
                        }
                    }
                    else
                    {
                        if (parameter.Cascading)
                        {
                            // Don't allow an "extra" cascading value to be collected - or don't allow a non-cascading
                            // parameter to be set with a cascading value.
                            //
                            // This is likely a bug in our infrastructure or an attempt to deliberately do something unsupported.
                            ThrowForSettingParameterWithCascadingValue(targetType, parameterName);
                            throw null; // Unreachable
                        }
                        else
                        {
                            unmatched ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                            unmatched[parameterName] = parameter.Value;
                        }
                    }
                }

                if (unmatched != null && isCaptureUnmatchedValuesParameterSetExplicitly)
                {
                    // This has to be an error because we want to allow users to set the CaptureUnmatchedValues
                    // parameter explicitly and ....
                    // 1. We don't ever want to mutate a value the user gives us.
                    // 2. We also don't want to implicitly copy a value the user gives us.
                    //
                    // Either one of those implementation choices would do something unexpected.
                    ThrowForCaptureUnmatchedValuesConflict(targetType, writers.CaptureUnmatchedValuesPropertyName!, unmatched);
                    throw null; // Unreachable
                }
                else if (unmatched != null)
                {
                    // We had some unmatched values, set the CaptureUnmatchedValues property
                    SetProperty(target, writers.CaptureUnmatchedValuesWriter, writers.CaptureUnmatchedValuesPropertyName!, unmatched, ref requiredParametersWritten);
                }
            }

            if (requiredParametersWritten != writers.RequiredParametersMap)
            {
                // Verify that we've written all the required parameters.
                ThrowRequiredParametersNotSet(targetType, parameters, writers);
            }

            static void SetProperty(object target, PropertySetter writer, string parameterName, object value, ref uint requiredParametersWritten)
            {
                try
                {
                    writer.SetValue(target, value);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Unable to set property '{parameterName}' on object of " +
                        $"type '{target.GetType().FullName}'. The error was: {ex.Message}", ex);
                }

                if (writer.Required)
                {
                    requiredParametersWritten |= (uint)(1 << writer.RequiredParameterId);
                }
            }
        }

        internal static IEnumerable<PropertyInfo> GetCandidateBindableProperties(Type targetType)
            => MemberAssignment.GetPropertiesIncludingInherited(targetType, _bindablePropertyFlags);

        [DoesNotReturn]
        private static void ThrowRequiredParametersNotSet(Type targetType, ParameterView parameters, WritersForType writers)
        {
            // We know we're going to throw by this stage, so it doesn't matter that the following
            // code is not optimized. We're just trying to help developers see what they did wrong.
            var parametersNotSet = new List<string>();
            var cascadingParametersNotSet = new List<string>();
            var parameterDictionary = parameters.ToDictionary();

            foreach (var (name, writer) in writers.Writers.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                if (!writer.Required || parameterDictionary.ContainsKey(name))
                {
                    // Either the value is not required or it's specified.
                    continue;
                }

                if (writer.Cascading)
                {
                    throw new InvalidOperationException($"Component '{targetType.FullName}' requires a value for the cascading parameter '{name}'.");
                }
                else
                {
                    throw new InvalidOperationException($"Component '{targetType.FullName}' requires a value for the parameter '{name}'.");
                }
            }

            Debug.Fail("Unreachable");
        }

        [DoesNotReturn]
        private static void ThrowForUnknownIncomingParameterName(Type targetType, string parameterName)
        {
            // We know we're going to throw by this stage, so it doesn't matter that the following
            // reflection code will be slow. We're just trying to help developers see what they did wrong.
            var propertyInfo = targetType.GetProperty(parameterName, _bindablePropertyFlags);
            if (propertyInfo != null)
            {
                if (!propertyInfo.IsDefined(typeof(ParameterAttribute)) && !propertyInfo.IsDefined(typeof(CascadingParameterAttribute)))
                {
                    throw new InvalidOperationException(
                        $"Object of type '{targetType.FullName}' has a property matching the name '{parameterName}', " +
                        $"but it does not have [{nameof(ParameterAttribute)}] or [{nameof(CascadingParameterAttribute)}] applied.");
                }
                else
                {
                    // This should not happen
                    throw new InvalidOperationException(
                        $"No writer was cached for the property '{propertyInfo.Name}' on type '{targetType.FullName}'.");
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"Object of type '{targetType.FullName}' does not have a property " +
                    $"matching the name '{parameterName}'.");
            }
        }

        [DoesNotReturn]
        private static void ThrowForSettingCascadingParameterWithNonCascadingValue(Type targetType, string parameterName)
        {
            throw new InvalidOperationException(
                $"Object of type '{targetType.FullName}' has a property matching the name '{parameterName}', " +
                $"but it does not have [{nameof(ParameterAttribute)}] applied.");
        }

        [DoesNotReturn]
        private static void ThrowForSettingParameterWithCascadingValue(Type targetType, string parameterName)
        {
            throw new InvalidOperationException(
                $"The property '{parameterName}' on component type '{targetType.FullName}' cannot be set " +
                $"using a cascading value.");
        }

        [DoesNotReturn]
        private static void ThrowForCaptureUnmatchedValuesConflict(Type targetType, string parameterName, Dictionary<string, object> unmatched)
        {
            throw new InvalidOperationException(
                $"The property '{parameterName}' on component type '{targetType.FullName}' cannot be set explicitly " +
                $"when also used to capture unmatched values. Unmatched values:" + Environment.NewLine +
                string.Join(Environment.NewLine, unmatched.Keys.OrderBy(k => k)));
        }

        [DoesNotReturn]
        private static void ThrowForMultipleCaptureUnmatchedValuesParameters(Type targetType)
        {
            // We don't care about perf here, we want to report an accurate and useful error.
            var propertyNames = targetType
                .GetProperties(_bindablePropertyFlags)
                .Where(p => p.GetCustomAttribute<ParameterAttribute>()?.CaptureUnmatchedValues == true)
                .Select(p => p.Name)
                .OrderBy(p => p)
                .ToArray();

            throw new InvalidOperationException(
                $"Multiple properties were found on component type '{targetType.FullName}' with " +
                $"'{nameof(ParameterAttribute)}.{nameof(ParameterAttribute.CaptureUnmatchedValues)}'. Only a single property " +
                $"per type can use '{nameof(ParameterAttribute)}.{nameof(ParameterAttribute.CaptureUnmatchedValues)}'. Properties:" + Environment.NewLine +
                string.Join(Environment.NewLine, propertyNames));
        }

        [DoesNotReturn]
        private static void ThrowForRequiredUnmatchedValuesParameter(Type targetType, string parameterName)
        {
            throw new InvalidOperationException(
                $"Parameter {parameterName} on component type '{targetType.FullName}' cannot have both '{nameof(ParameterAttribute.CaptureUnmatchedValues)}' " +
                $"and {nameof(ParameterAttribute.Required)} set.");
        }

        [DoesNotReturn]
        private static void ThrowForInvalidCaptureUnmatchedValuesParameterType(Type targetType, PropertyInfo propertyInfo)
        {
            throw new InvalidOperationException(
                $"The property '{propertyInfo.Name}' on component type '{targetType.FullName}' cannot be used " +
                $"with '{nameof(ParameterAttribute)}.{nameof(ParameterAttribute.CaptureUnmatchedValues)}' because it has the wrong type. " +
                $"The property must be assignable from 'Dictionary<string, object>'.");
        }

        private class WritersForType
        {
            private const int MaxCachedWriterLookups = 100;
            private readonly Dictionary<string, PropertySetter> _underlyingWriters;
            private readonly ConcurrentDictionary<string, PropertySetter?> _referenceEqualityWritersCache;

            public WritersForType(Type targetType)
            {
                _underlyingWriters = new Dictionary<string, PropertySetter>(StringComparer.OrdinalIgnoreCase);
                _referenceEqualityWritersCache = new ConcurrentDictionary<string, PropertySetter?>(ReferenceEqualityComparer.Instance);

                var requiredParameterId = 0;

                foreach (var propertyInfo in GetCandidateBindableProperties(targetType))
                {
                    var parameterAttribute = propertyInfo.GetCustomAttribute<ParameterAttribute>();
                    var cascadingParameterAttribute = propertyInfo.GetCustomAttribute<CascadingParameterAttribute>();
                    var isParameter = parameterAttribute != null || cascadingParameterAttribute != null;
                    if (!isParameter)
                    {
                        continue;
                    }

                    var propertyName = propertyInfo.Name;
                    if (parameterAttribute != null && (propertyInfo.SetMethod == null || !propertyInfo.SetMethod.IsPublic))
                    {
                        throw new InvalidOperationException(
                            $"The type '{targetType.FullName}' declares a parameter matching the name '{propertyName}' that is not public. Parameters must be public.");
                    }

                    PropertySetter propertySetter;
                    if (cascadingParameterAttribute != null)
                    {
                        propertySetter = new PropertySetter(targetType, propertyInfo)
                        {
                            RequiredParameterId = requiredParameterId,
                            Cascading = true,
                            Required = cascadingParameterAttribute.Required,
                        };
                    }
                    else
                    {
                        propertySetter = new PropertySetter(targetType, propertyInfo)
                        {
                            Cascading = false,
                            Required = parameterAttribute!.Required
                        };
                    }

                    if (propertySetter.Required)
                    {
                        if (requiredParameterId == 32)
                        {
                            // Our bit-mask only allows up to 32 values. We think it's uncommon to need this many parameters, so fail loudly
                            // if this limit is exceeded.
                            throw new NotSupportedException($"The component '{targetType.FullName}' declares more than 32 'required' parameters. A component may have at most 32 required parameters.");
                        }

                        RequiredParametersMap |= (uint)(1 << requiredParameterId);
                        propertySetter.RequiredParameterId = requiredParameterId++;
                    }

                    if (_underlyingWriters.ContainsKey(propertyName))
                    {
                        throw new InvalidOperationException(
                            $"The type '{targetType.FullName}' declares more than one parameter matching the " +
                            $"name '{propertyName.ToLowerInvariant()}'. Parameter names are case-insensitive and must be unique.");
                    }

                    _underlyingWriters.Add(propertyName, propertySetter);

                    if (parameterAttribute != null && parameterAttribute.CaptureUnmatchedValues)
                    {
                        // This is an "Extra" parameter.
                        //
                        // There should only be one of these.
                        if (CaptureUnmatchedValuesWriter != null)
                        {
                            ThrowForMultipleCaptureUnmatchedValuesParameters(targetType);
                        }

                        // It must be able to hold a Dictionary<string, object> since that's what we create.
                        if (!propertyInfo.PropertyType.IsAssignableFrom(typeof(Dictionary<string, object>)))
                        {
                            ThrowForInvalidCaptureUnmatchedValuesParameterType(targetType, propertyInfo);
                        }

                        if (parameterAttribute.Required)
                        {
                            ThrowForRequiredUnmatchedValuesParameter(targetType, propertyName);
                        }

                        CaptureUnmatchedValuesWriter = new PropertySetter(targetType, propertyInfo)
                        {
                            Cascading = true,
                            Required = false
                        };
                        CaptureUnmatchedValuesPropertyName = propertyInfo.Name;
                    }
                }
            }

            public uint RequiredParametersMap { get; }

            public PropertySetter? CaptureUnmatchedValuesWriter { get; }

            public string? CaptureUnmatchedValuesPropertyName { get; }

            public IReadOnlyDictionary<string, PropertySetter> Writers => _underlyingWriters;

            public bool TryGetValue(string parameterName, [MaybeNullWhen(false)] out PropertySetter writer)
            {
                // In intensive parameter-passing scenarios, one of the most expensive things we do is the
                // lookup from parameterName to writer. Pre-5.0 that was because of the string hashing.
                // To optimize this, we now have a cache in front of the lookup which is keyed by parameterName's
                // object identity (not its string hash). So in most cases we can resolve the lookup without
                // having to hash the string. We only fall back on hashing the string if the cache gets full,
                // which would only be in very unusual situations because components don't typically have many
                // parameters, and the parameterName strings usually come from compile-time constants.
                if (!_referenceEqualityWritersCache.TryGetValue(parameterName, out writer))
                {
                    _underlyingWriters.TryGetValue(parameterName, out writer);

                    // Note that because we're not locking around this, it's possible we might
                    // actually write more than MaxCachedWriterLookups entries due to concurrent
                    // writes. However this won't cause any problems.
                    // Also note that the value we're caching might be 'null'. It's valid to cache
                    // lookup misses just as much as hits, since then we can more quickly identify
                    // incoming values that don't have a corresponding writer and thus will end up
                    // being passed as catch-all parameter values.
                    if (_referenceEqualityWritersCache.Count < MaxCachedWriterLookups)
                    {
                        _referenceEqualityWritersCache.TryAdd(parameterName, writer);
                    }
                }

                return writer != null;
            }
        }
    }
}
