using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tk2Gui
{
    /// <summary>変速機付きの車の値のセット (車両形式ごと)。ギア比・最終減速比・タイヤ・気筒数・回転計の目盛り</summary>
    public sealed class CarPreset
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("cylinders")] public int Cylinders { get; set; } = 4;
        [JsonPropertyName("gearRatios")] public double[] GearRatios { get; set; }
        [JsonPropertyName("finalRatio")] public double FinalRatio { get; set; }
        [JsonPropertyName("tireSize")] public string TireSize { get; set; }
        [JsonPropertyName("tireCircM")] public double TireCircM { get; set; }
        [JsonPropertyName("tachoMax")] public int TachoMax { get; set; }
        [JsonPropertyName("tachoYellow")] public int TachoYellow { get; set; }
        [JsonPropertyName("tachoRed")] public int TachoRed { get; set; }

        public CarPreset Clone() { CarPreset p = (CarPreset)MemberwiseClone(); p.GearRatios = GearRatios != null ? (double[])GearRatios.Clone() : null; return p; }

        /// <summary>解析結果の車の値へ写す (使う一番上のギアは走る場所で変わるので写さない)</summary>
        public void ApplyTo(EngineStore.FileData f)
        {
            f.Vehicle = "geared"; f.CarModel = Name;
            f.Cylinders = Cylinders; f.GearRatios = GearRatios != null ? (double[])GearRatios.Clone() : null;
            f.FinalRatio = FinalRatio; f.TireSize = TireSize; f.TireCircM = TireCircM;
            f.TachoMax = TachoMax; f.TachoYellow = TachoYellow; f.TachoRed = TachoRed;
        }

        public static CarPreset From(EngineStore.FileData f, string name)
        {
            return new CarPreset
            {
                Name = name, Cylinders = f.Cylinders, GearRatios = f.GearRatios != null ? (double[])f.GearRatios.Clone() : null,
                FinalRatio = f.FinalRatio, TireSize = f.TireSize, TireCircM = f.TireCircM,
                TachoMax = f.TachoMax, TachoYellow = f.TachoYellow, TachoRed = f.TachoRed
            };
        }
    }

    /// <summary>
    /// 車両のセットの一覧。最初から入っている純正の値 (DC5・EK9・EF8) と、ユーザーが保存したもの。
    /// 保存先は exe と同じフォルダの WintecTK2Converter.cars.json (書き込めなければ %LOCALAPPDATA%\WintecTK2Converter\)。
    /// 同じ名前で保存すると、最初から入っている値より優先する (レース車両でファイナルやタイヤを変えたときなど)
    /// </summary>
    public static class CarPresets
    {
        public static readonly CarPreset[] BuiltIn =
        {
            // DC5 インテグラ Type R (K20A、6MT)。j-garage・itr-dc5.club の諸元
            new CarPreset { Name = "DC5 インテグラ Type R", Cylinders = 4, GearRatios = new[] { 3.266, 2.130, 1.517, 1.212, 0.972, 0.780 },
                            FinalRatio = 4.764, TireSize = "215/45R17", TachoMax = 9000, TachoYellow = 7500, TachoRed = 8200 },
            // EK9 シビック Type R (B16B、5MT)。j-garage の諸元 (1998 年式)
            new CarPreset { Name = "EK9 シビック Type R", Cylinders = 4, GearRatios = new[] { 3.230, 2.105, 1.458, 1.107, 0.848 },
                            FinalRatio = 4.400, TireSize = "195/55R15", TachoMax = 10000, TachoYellow = 8000, TachoRed = 8400 },
            // EF8 CR-X SiR (B16A、5MT、Y1)。cr-x.org・goo-net の諸元
            new CarPreset { Name = "EF8 CR-X SiR", Cylinders = 4, GearRatios = new[] { 3.166, 2.052, 1.416, 1.103, 0.870 },
                            FinalRatio = 4.266, TireSize = "195/60R14", TachoMax = 9000, TachoYellow = 7500, TachoRed = 8000 },
        };

        sealed class Data
        {
            [JsonPropertyName("format")] public string Format { get; set; } = "WintecTK2Cars";
            [JsonPropertyName("cars")] public List<CarPreset> Cars { get; set; } = new List<CarPreset>();
        }

        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        const string FileName = "WintecTK2Converter.cars.json";
        public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, FileName);
        static readonly string FallbackPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WintecTK2Converter", FileName);

        static Data _user;

        static Data Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                Data d = JsonSerializer.Deserialize<Data>(File.ReadAllText(path, Encoding.UTF8), Opt);
                if (d == null || d.Format != "WintecTK2Cars") return null;
                if (d.Cars == null) d.Cars = new List<CarPreset>();
                d.Cars.RemoveAll(c => c == null || string.IsNullOrWhiteSpace(c.Name));
                return d;
            }
            catch (Exception) { return null; }
        }

        static Data User { get { return _user ?? (_user = Read(FilePath) ?? Read(FallbackPath) ?? new Data()); } }

        static void Write()
        {
            string json = JsonSerializer.Serialize(User, Opt);
            try { File.WriteAllText(FilePath, json, new UTF8Encoding(false)); return; }
            catch (Exception) { }
            Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath));
            File.WriteAllText(FallbackPath, json, new UTF8Encoding(false));
        }

        /// <summary>選べるセットの一覧 (最初から入っているものの順、続けてユーザーが足したもの)。同じ名前はユーザーの値</summary>
        public static List<CarPreset> All()
        {
            List<CarPreset> r = new List<CarPreset>();
            foreach (CarPreset b in BuiltIn)
            {
                CarPreset u = User.Cars.FirstOrDefault(c => c.Name == b.Name);
                r.Add((u ?? b).Clone());
            }
            foreach (CarPreset u in User.Cars)
                if (!BuiltIn.Any(b => b.Name == u.Name)) r.Add(u.Clone());
            return r;
        }

        public static CarPreset Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return All().FirstOrDefault(c => c.Name == name);
        }

        public static bool IsBuiltIn(string name) { return BuiltIn.Any(b => b.Name == name); }
        public static bool IsUserSaved(string name) { return User.Cars.Any(c => c.Name == name); }

        /// <summary>同じ名前があれば置き換えて保存</summary>
        public static void Save(CarPreset p)
        {
            User.Cars.RemoveAll(c => c.Name == p.Name);
            User.Cars.Add(p.Clone());
            Write();
        }

        /// <summary>ユーザーの保存を消す (最初から入っている名前なら純正の値に戻る)</summary>
        public static void Delete(string name)
        {
            User.Cars.RemoveAll(c => c.Name == name);
            Write();
        }
    }
}
