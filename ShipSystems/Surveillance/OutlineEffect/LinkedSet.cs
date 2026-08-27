using System.Collections;
using System.Collections.Generic;

namespace Y4NGZCompany.ShipSystems.Surveillance.OutlineEffect
{
    public sealed class LinkedSet<T> : IEnumerable<T>
    {
        private readonly LinkedList<T> _list = new LinkedList<T>();
        private readonly Dictionary<T, LinkedListNode<T>> _dictionary = new Dictionary<T, LinkedListNode<T>>();

        public int Count => _list.Count;

        public bool Add(T item)
        {
            if (_dictionary.ContainsKey(item))
                return false;

            LinkedListNode<T> node = _list.AddLast(item);
            _dictionary.Add(item, node);
            return true;
        }

        public bool Remove(T item)
        {
            if (!_dictionary.TryGetValue(item, out LinkedListNode<T> node))
                return false;

            _dictionary.Remove(item);
            _list.Remove(node);
            return true;
        }

        public bool Contains(T item)
        {
            return _dictionary.ContainsKey(item);
        }

        public IEnumerator<T> GetEnumerator()
        {
            return _list.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
