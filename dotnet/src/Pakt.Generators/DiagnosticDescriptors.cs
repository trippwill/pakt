using Microsoft.CodeAnalysis;

namespace Pakt.Generators
{
    internal static class DiagnosticDescriptors
    {
        public static readonly DiagnosticDescriptor ConverterNotSupported = new DiagnosticDescriptor(
            "PAKT001",
            "PaktConverter is not yet supported",
            "[PaktConverter] on property '{0}' is not supported in this version of the generator",
            "PaktGenerator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor TypeNotRegistered = new DiagnosticDescriptor(
            "PAKT002",
            "Nested type not registered",
            "Property '{0}' references type '{1}' which is not registered with [PaktSerializable]. Serialization will be skipped for this property.",
            "PaktGenerator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor NoSettableSetter = new DiagnosticDescriptor(
            "PAKT003",
            "Property has no settable setter",
            "Property '{0}' on type '{1}' has no accessible setter and will be skipped during deserialization",
            "PaktGenerator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MixedPropertyOrder = new DiagnosticDescriptor(
            "PAKT004",
            "Mixed property ordering",
            "Type '{0}' has some properties with [PaktPropertyOrder] and some without. Either all or none must use explicit ordering.",
            "PaktGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
