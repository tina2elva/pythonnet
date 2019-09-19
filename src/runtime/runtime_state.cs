using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Linq;
using static Python.Runtime.Runtime;

namespace Python.Runtime
{
    class RuntimeState
    {
        public static void Save()
        {
            if (PySys_GetObject("dummy_gc") != IntPtr.Zero)
            {
                throw new Exception("Runtime State set already");
            }

            var modules = PySet_New(IntPtr.Zero);
            foreach (var name in GetModuleNames())
            {
                int res = PySet_Add(modules, name);
                PythonException.ThrowIfIsNotZero(res);
            }

            var objs = PySet_New(IntPtr.Zero);
            foreach (var obj in PyGCGetObjects())
            {
                AddObjPtrToSet(objs, obj);
            }

            var dummyGCHead = PyMem_Malloc(Marshal.SizeOf(typeof(PyGC_Head)));
            unsafe
            {
                var head = (PyGC_Head*)dummyGCHead;
                head->gc.gc_next = dummyGCHead;
                head->gc.gc_prev = dummyGCHead;
                head->gc.gc_refs = IntPtr.Zero;
            }
            {
                var pyDummyGC = PyLong_FromVoidPtr(dummyGCHead);
                int res = PySys_SetObject("dummy_gc", pyDummyGC);
                PythonException.ThrowIfIsNotZero(res);
                XDecref(pyDummyGC);

                AddObjPtrToSet(objs, modules);
                try
                {
                    res = PySys_SetObject("initial_modules", modules);
                    PythonException.ThrowIfIsNotZero(res);
                }
                finally
                {
                    XDecref(modules);
                }

                AddObjPtrToSet(objs, objs);
                try
                {
                    res = PySys_SetObject("initial_objs", objs);
                    PythonException.ThrowIfIsNotZero(res);
                }
                finally
                {
                    XDecref(objs);
                }

            }
        }

        public static void Restore()
        {
            var dummyGCAddr = PySys_GetObject("dummy_gc");
            if (dummyGCAddr == IntPtr.Zero)
            {
                throw new Exception("Runtime state have not set");
            }
            var dummyGC = PyLong_AsVoidPtr(dummyGCAddr);
            ResotreModules(dummyGC);
            RestoreObjects(dummyGC);
        }

        private static void ResotreModules(IntPtr dummyGC)
        {
            var intialModules = PySys_GetObject("initial_modules");
            Debug.Assert(intialModules != null);
            var modules = PyImport_GetModuleDict();
            foreach (var name in GetModuleNames())
            {
                if (PySet_Contains(intialModules, name) == 1)
                {
                    continue;
                }
                var module = PyDict_GetItem(modules, name);
                if (_PyObject_GC_IS_TRACKED(module))
                {
                    ExchangeGCChain(module, dummyGC);
                }
                PyDict_DelItem(modules, name);
            }
        }

        private static void RestoreObjects(IntPtr dummyGC)
        {
            var intialObjs = PySys_GetObject("initial_objs");
            Debug.Assert(intialObjs != null);
            foreach (var obj in PyGCGetObjects())
            {
                var p = PyLong_FromVoidPtr(obj);
                try
                {
                    if (PySet_Contains(intialObjs, p) == 1)
                    {
                        continue;
                    }
                }
                finally
                {
                    XDecref(p);
                }
                Debug.Assert(_PyObject_GC_IS_TRACKED(obj), "A GC object must be tracked");
                ExchangeGCChain(obj, dummyGC);
            }
        }

        public static IEnumerable<IntPtr> PyGCGetObjects()
        {
            var gc = PyImport_ImportModule("gc");
            var get_objects = PyObject_GetAttrString(gc, "get_objects");
            var objs = PyObject_CallObject(get_objects, IntPtr.Zero);
            var length = PyList_Size(objs);
            for (long i = 0; i < length; i++)
            {
                var obj = PyList_GetItem(objs, i);
                yield return obj;
            }
            XDecref(objs);
            XDecref(gc);
        }

        public static IEnumerable<IntPtr> GetModuleNames()
        {
            var modules = PyImport_GetModuleDict();
            var names = PyDict_Keys(modules);
            var length = PyList_Size(names);
            for (int i = 0; i < length; i++)
            {
                var name = PyList_GetItem(names, i);
                yield return name;
            }
        }

        private static void AddObjPtrToSet(IntPtr set, IntPtr obj)
        {
            var p = PyLong_FromVoidPtr(obj);
            XIncref(obj);
            try
            {
                int res = PySet_Add(set, p);
                PythonException.ThrowIfIsNotZero(res);
            }
            finally
            {
                XDecref(p);
            }
        }
        /// <summary>
        /// Exchange gc to a dummy gc prevent nullptr error in _PyObject_GC_UnTrack macro.
        /// </summary>
        private static void ExchangeGCChain(IntPtr obj, IntPtr gc)
        {
            var head = _Py_AS_GC(obj);
            if ((long)_PyGCHead_REFS(head) == _PyGC_REFS_UNTRACKED)
            {
                throw new Exception("GC object untracked");
            }
            unsafe
            {
                var g = (PyGC_Head*)head;
                var newGCGen = (PyGC_Head*)gc;

                ((PyGC_Head*)g->gc.gc_prev)->gc.gc_next = g->gc.gc_next;
                ((PyGC_Head*)g->gc.gc_next)->gc.gc_prev = g->gc.gc_prev;

                g->gc.gc_next = gc;
                g->gc.gc_prev = newGCGen->gc.gc_prev;
                ((PyGC_Head*)g->gc.gc_prev)->gc.gc_next = head;
                newGCGen->gc.gc_prev = head;
            }
        }

        private static IEnumerable<IntPtr> IterGCNodes(IntPtr gc)
        {
            var node = GetNextGCNode(gc);
            while (node != gc)
            {
                var next = GetNextGCNode(node);
                yield return node;
                node = next;
            }
        }

        private static IEnumerable<IntPtr> IterObjects(IntPtr gc)
        {
            foreach (var node in IterGCNodes(gc))
            {
                yield return _Py_FROM_GC(node);
            }
        }

        private static unsafe IntPtr GetNextGCNode(IntPtr node)
        {
            return ((PyGC_Head*)node)->gc.gc_next;
        }
    }
}
