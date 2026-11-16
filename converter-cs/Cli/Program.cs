// co-cli - command line for testing the converter.
//   co-cli convert <mod> <output> <slug> "<Name>" [game_folder]
//   co-cli analyze <archive_or_folder> [game_folder]
//   co-cli build <archive_or_folder> <output.zip> [game_folder]
//   co-cli re3tables <output_dir>   (RE3 costume tables for the script)
using System;
using System.Linq;

namespace CustomOutfits.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                string cmd = args.Length > 0 ? args[0] : "";
                string Game(int i) => args.Length > i ? args[i] : Core.DetectGameDir();
                switch (cmd)
                {
                    case "convert":
                    {
                        var rep = Converter.Convert(args[1], args[2], args[3], args[4], new ConvertOptions { GameDir = Game(5) });
                        foreach (var u in rep.Units)
                            Console.WriteLine($"{u.Character,-7} {u.Part,-5} {u.Dir,-18} \"{u.V.Label}\" direct:[{string.Join(",", u.V.Direct)}] costumes:[{string.Join(",", u.V.Costumes)}] mdf2:{u.MdfSource}");
                        Console.WriteLine("copied: " + rep.Copied);
                        foreach (var w in rep.Warnings) Console.WriteLine("WARNING: " + w);
                        return 0;
                    }
                    case "analyze":
                    {
                        var p = Core.Analyze(args[1], Game(2));
                        Console.WriteLine("package: " + p.PackageName);
                        foreach (var o in p.Options)
                            foreach (var x in o.Outfits)
                                Console.WriteLine($"  {x.CharacterName} / {x.PartName} => \"{x.Name}\" [{string.Join(", ", x.Variants)}] slug {o.Slug}");
                        foreach (var w in p.Warnings) Console.WriteLine("WARNING: " + w);
                        Core.Cleanup(p);
                        return 0;
                    }
                    case "build":
                    {
                        var p = Core.Analyze(args[1], Game(3));
                        var r = Core.Build(p, args[2], false);
                        Core.Cleanup(p);
                        Console.WriteLine($"ok: {r.Output} ({r.Outfits} outfits)");
                        foreach (var w in r.Warnings) Console.WriteLine("WARNING: " + w);
                        return 0;
                    }
                    case "re3tables":
                    {
                        // RE3 costume tables for the script: reframework/data/CustomOutfits/game/*.json
                        Re3.WriteGameTables(args[1]);
                        Console.WriteLine("ok: " + string.Join(", ", Re3.Models.Select(m => m.Id)));
                        return 0;
                    }
                    default:
                        Console.WriteLine("usage: co-cli convert|analyze|build|re3tables ...");
                        return 2;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("ERROR: " + e.Message);
                return 1;
            }
        }
    }
}
