using System.Numerics;
using ForgeSkin.Backend;
using Xunit;

namespace ForgeSkin.Tests;

// Detection ability of assembly.verify: a readback equal to the backend assembly passes, a shifted one is caught.
public class AssemblyVerifyTests
{
    static Node Holder()
    {
        var holder = new Node { Name = "Gear" };
        var stock = new Node { Name = "Stock_Pistol_1", Part = "stock", Position = new(0, 0.01f, -0.05f) };
        stock.Attach(holder);
        new Node { Name = "a_mag", Position = new(0, -0.0087f, -0.0552f), Rotation = Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, 0) }.Attach(stock);
        var mag = new Node { Name = "Mag", Part = "mag" };
        mag.Attach(stock.Children[0]);
        new Node { Name = "Cartridge1", Position = new(0, 0.02f, 0), Scale = new(1, 1, 1.5f) }.Attach(mag);
        return holder;
    }

    static Readback ReadbackOf(Node holder) => new(66, "test", Assembly.PartNodes(holder, activeOnly: false).Select(x =>
    {
        Matrix4x4.Decompose(x.Node.World, out var s, out var r, out var t);
        return new ReadbackNode(x.Part, x.Path, [t.X, t.Y, t.Z], [r.X, r.Y, r.Z, r.W], [s.X, s.Y, s.Z]);
    }).ToList());

    [Fact]
    public void ShiftedReadbackIsCaught()
    {
        var holder = Holder();
        var exact = ReadbackOf(holder);
        Assert.Contains(exact.Nodes, n => n.Part == "stock" && n.Path == "a_mag");
        Assert.Contains(exact.Nodes, n => n.Part == "mag" && n.Path == "Cartridge1");
        Assert.True(Verify.Compare("exact", exact, "test", holder).Pass);

        var shifted = ReadbackOf(holder);
        shifted.Nodes[^1].Position[2] += 0.002f; // 2 mm
        var result = Verify.Compare("shifted", shifted, "test", holder);
        Assert.False(result.Pass);
        Assert.Equal(("mag", "Cartridge1"), (result.Misses[0].Part, result.Misses[0].Path));
    }
}
