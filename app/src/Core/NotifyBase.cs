using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Grappler.Core
{
    /// <summary>
    /// Minimal INotifyPropertyChanged base. CallerMemberName is a C# 5
    /// feature, so it survives the old compiler this project builds with.
    /// </summary>
    public abstract class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string property = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(property));
            }
        }

        /// <summary>Assigns and notifies only when the value actually moved.</summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string property = null)
        {
            if (object.Equals(field, value)) { return false; }
            field = value;
            Raise(property);
            return true;
        }
    }
}
