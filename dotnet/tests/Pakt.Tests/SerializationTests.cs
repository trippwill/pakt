using Pakt.Serialization;

namespace Pakt.Tests;

public class AttributeTests
{
    [Fact]
    public void PaktSerializable_StoresType()
    {
        var attr = new PaktSerializableAttribute(typeof(string));
        Assert.Equal(typeof(string), attr.Type);
    }

    [Fact]
    public void PaktProperty_StoresName()
    {
        var attr = new PaktPropertyAttribute("host_name");
        Assert.Equal("host_name", attr.Name);
    }

    [Fact]
    public void PaktAtom_StoresMembers()
    {
        var attr = new PaktAtomAttribute("dev", "staging", "prod");
        Assert.Equal(["dev", "staging", "prod"], attr.Members);
    }

    [Fact]
    public void PaktPropertyOrder_StoresOrder()
    {
        var attr = new PaktPropertyOrderAttribute(3);
        Assert.Equal(3, attr.Order);
    }

    [Fact]
    public void PaktConverter_StoresConverterType()
    {
        var attr = new PaktConverterAttribute(typeof(object));
        Assert.Equal(typeof(object), attr.ConverterType);
    }

    [Fact]
    public void PaktScalar_StoresScalarType()
    {
        var attr = new PaktScalarAttribute(PaktScalarType.Ts);
        Assert.Equal(PaktScalarType.Ts, attr.ScalarType);
    }
}

public class SerializationMetadataTests
{
    [Fact]
    public void PaktTypeInfo_StoresMetadata()
    {
        var type = PaktType.Scalar(PaktScalarType.Str);
        var props = new List<PaktPropertyInfo>
        {
            new("Host", "host", typeof(string), PaktType.Scalar(PaktScalarType.Str), 0),
            new("Port", "port", typeof(int), PaktType.Scalar(PaktScalarType.Int), 1),
        };

        var info = new PaktTypeInfo<object>(type, props);

        Assert.Equal(type, info.PaktType);
        Assert.Equal(2, info.Properties.Count);
    }

    [Fact]
    public void PaktPropertyInfo_StoresAllFields()
    {
        var propType = PaktType.Scalar(PaktScalarType.Bool);
        var info = new PaktPropertyInfo("IsActive", "is_active", typeof(bool), propType, 2, isIgnored: false);

        Assert.Equal("IsActive", info.ClrName);
        Assert.Equal("is_active", info.PaktName);
        Assert.Equal(typeof(bool), info.ClrType);
        Assert.Equal(propType, info.PaktType);
        Assert.Equal(2, info.Order);
        Assert.False(info.IsIgnored);
    }

    [Fact]
    public void PaktPropertyInfo_Ignored()
    {
        var info = new PaktPropertyInfo("Skip", "skip", typeof(string), PaktType.Scalar(PaktScalarType.Str), 0, isIgnored: true);
        Assert.True(info.IsIgnored);
    }

    [Fact]
    public void DeserializeOptions_Default_NotNull()
    {
        Assert.NotNull(DeserializeOptions.Default);
        Assert.Empty(DeserializeOptions.Default.Converters);
    }
}
