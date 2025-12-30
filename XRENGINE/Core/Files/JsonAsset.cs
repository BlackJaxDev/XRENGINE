using Newtonsoft.Json;
using XREngine.Data;

namespace XREngine.Core.Files
{
    /// <summary>
    /// Automatically serializes and deserializes a class to and from JSON format.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [XR3rdPartyExtensions(typeof(XREngine.Data.XRDefault3rdPartyImportOptions), "json")]
    public class JsonAsset<T> : TextFile where T : class, new()
    {
        public JsonAsset()
        {
            _data = new T();
            Text = JsonConvert.SerializeObject(_data, Formatting.Indented);
        }
        public JsonAsset(string path)
            : base(path)
        {
            _data = new T();
            Text = JsonConvert.SerializeObject(_data, Formatting.Indented);
        }
        public JsonAsset(T data)
        {
            _data = data;
            Text = data is not null ? JsonConvert.SerializeObject(data, Formatting.Indented) : string.Empty;
        }

        private T _data;
        public T Data
        {
            get => _data;
            set => SetField(ref _data, value);
        }

        private bool _converting = false;

        protected override void OnPropertyChanged<T2>(string? propName, T2 prev, T2 field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Text):
                    ConvTextToData();
                    break;
                case nameof(Data):
                    ConvDataToText();
                    break;
            }
        }

        private void ConvTextToData()
        {
            if (_converting)
                return;

            try
            {
                _converting = true;
                Data = string.IsNullOrEmpty(Text) ? new T() : JsonConvert.DeserializeObject<T>(Text) ?? new T();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to load json: {e.Message}{Environment.NewLine}{Text}");
            }
            finally
            {
                _converting = false;
            }
        }

        private void ConvDataToText()
        {
            if (_converting)
                return;

            try
            {
                _converting = true;
                Text = Data is not null ? JsonConvert.SerializeObject(Data, Formatting.Indented) : string.Empty;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to save json: {e.Message}{Environment.NewLine}{Data}");
            }
            finally
            {
                _converting = false;
            }
        }
    }
}