using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FeedCustomizer.Models
{
    public partial class ObservableValue<T>(T initialValue = default!, Action? _afterSet = null) : INotifyPropertyChanged
    {
        private T _value = initialValue;

        public Action? AfterSet { get; set; } = _afterSet;

        public event PropertyChangedEventHandler? PropertyChanged;

        public T Value
        {
            get => _value;
            set
            {
                if (!EqualityComparer<T>.Default.Equals(_value, value))
                {
                    _value = value;
                    OnPropertyChanged();
                    AfterSet?.Invoke();
                }
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // 隐式转换，方便使用
        public static implicit operator T(ObservableValue<T> observable) => observable.Value;
        public static implicit operator ObservableValue<T>(T value) => new(value);
    }
}
