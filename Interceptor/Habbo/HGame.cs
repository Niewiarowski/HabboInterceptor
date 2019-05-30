using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;

using Flazzy;
using Flazzy.IO;
using Flazzy.ABC;
using Flazzy.Tags;
using Flazzy.Records;
using Flazzy.ABC.AVM2;
using Flazzy.ABC.AVM2.Instructions;

using static Interceptor.Habbo.HMessage;

namespace Interceptor.Habbo
{
    public class HGame : ShockwaveFlash
    {
        private static readonly string[] _reservedNames = new[]
        {
            "break", "case", "catch", "class", "continue",
            "default", "do", "dynamic", "each", "else",
            "extends", "false", "final", "finally", "for",
            "function", "get", "if", "implements", "import",
            "in", "include", "native", "null", "override",
            "package", "return", "set", "static", "super",
            "switch", "throw", "true", "try", "use",
            "var", "while", "with"
        };

        private readonly Dictionary<DoABCTag, ABCFile> _abcFileTags;
        private readonly Dictionary<ASClass, HMessage> _messages;

        public List<ABCFile> ABCFiles { get; }
        public bool IsPostShuffle { get; private set; } = true;

        public SortedDictionary<ushort, HMessage> InMessages { get; }
        public SortedDictionary<ushort, HMessage> OutMessages { get; }
        public SortedDictionary<string, List<HMessage>> Messages { get; }

        public string Location { get; set; }

        public HGame(string path)
            : this(File.OpenRead(path))
        {
            Location = path;
        }
        public HGame(byte[] data)
            : this(new MemoryStream(data))

        { }
        public HGame(Stream input)
            : this(input, false)
        { }
        public HGame(Stream input, bool leaveOpen)
            : this(new FlashReader(input, leaveOpen))
        { }

        protected HGame(FlashReader input)
            : base(input)
        {
            _abcFileTags = new Dictionary<DoABCTag, ABCFile>();
            _messages = new Dictionary<ASClass, HMessage>();

            ABCFiles = new List<ABCFile>();
            InMessages = new SortedDictionary<ushort, HMessage>();
            OutMessages = new SortedDictionary<ushort, HMessage>();
            Messages = new SortedDictionary<string, List<HMessage>>();
        }

        public void GenerateMessageHashes()
        {
            FindMessagesReferences();
            foreach (HMessage message in OutMessages.Values.Concat(InMessages.Values))
            {
                if (!Messages.TryGetValue(message.GenerateHash(), out var @group))
                {
                    group = new List<HMessage>();
                    Messages.Add(message.Hash, group);
                }
                group.Add(message);
            }
        }

        #region Message Reference Searching
        private void FindMessagesReferences()
        {
            int classRank = 1;
            ABCFile abc = ABCFiles.Last();
            foreach (ASClass @class in abc.Classes)
            {
                ASInstance instance = @class.Instance;
                if (_messages.ContainsKey(@class)) continue;
                if (instance.Flags.HasFlag(ClassFlags.Interface)) continue;

                IEnumerable<ASMethod> methods = (new[] { @class.Constructor, instance.Constructor })
                    .Concat(instance.GetMethods())
                    .Concat(@class.GetMethods());

                int methodRank = 0;
                foreach (ASMethod fromMethod in methods)
                {
                    bool isStatic = (fromMethod.Trait?.IsStatic ?? @class.Constructor == fromMethod);
                    var fromContainer = (isStatic ? (ASContainer)@class : instance);

                    List<HReference> refernces = FindMessageReferences(@class, fromContainer, fromMethod);
                    if (refernces.Count > 0)
                    {
                        methodRank++;
                    }
                    foreach (HReference reference in refernces)
                    {
                        reference.IsStatic = isStatic;
                        reference.ClassRank = classRank;
                        reference.MethodRank = methodRank;
                        reference.GroupCount = refernces.Count;
                    }
                }
                if (methodRank > 0)
                {
                    classRank++;
                }
            }

            var froms = new Dictionary<ASContainer, List<HReference>>();
            foreach (HMessage incomingMsg in InMessages.Values)
            {
                foreach (HReference reference in incomingMsg.References)
                {
                    if (!froms.TryGetValue(reference.FromMethod.Container, out var references))
                    {
                        references = new List<HReference>();
                        froms.Add(reference.FromMethod.Container, references);
                    }
                    if (!references.Contains(reference))
                    {
                        references.Add(reference);
                    }
                }
            }

            classRank = 1;
            foreach (ASClass @class in abc.Classes)
            {
                ASContainer container = null;
                if (froms.TryGetValue(@class, out var references))
                {
                    container = @class;
                }
                else if (froms.TryGetValue(@class.Instance, out references))
                {
                    container = @class.Instance;
                }
                if (container != null)
                {
                    var methodReferenceGroups = new Dictionary<ASMethod, List<HReference>>();
                    foreach (HReference reference in references)
                    {
                        reference.FromClass = @class;
                        reference.InstructionRank = -1;
                        reference.ClassRank = classRank;
                        reference.IsStatic = container.IsStatic;
                        reference.GroupCount = references.Count;

                        if (!methodReferenceGroups.TryGetValue(reference.FromMethod, out var methodReferences))
                        {
                            methodReferences = new List<HReference>();
                            methodReferenceGroups.Add(reference.FromMethod, methodReferences);
                        }
                        methodReferences.Add(reference);
                    }

                    int methodRank = 1;
                    foreach (ASMethod method in container.GetMethods())
                    {
                        if (methodReferenceGroups.TryGetValue(method, out var methodReferences))
                        {
                            foreach (HReference reference in methodReferences)
                            {
                                reference.MethodRank = methodRank;
                            }
                            methodRank++;
                        }
                    }
                    classRank++;
                }
            }
        }

        private List<HReference> FindMessageReferences(ASClass fromClass, ASContainer fromContainer, ASMethod fromMethod)
        {
            int instructionRank = 0;
            ABCFile abc = fromMethod.GetABC();

            var nameStack = new Stack<ASMultiname>();
            var references = new List<HReference>();

            ASContainer container = null;
            ASCode code = fromMethod.Body.ParseCode();
            for (int i = 0; i < code.Count; i++)
            {
                int extraNamePopCount = 0;
                ASInstruction instruction = code[i];
                switch (instruction.OP)
                {
                    default: continue;
                    case OPCode.NewFunction:
                        {
                            var newFunction = (NewFunctionIns)instruction;
                            references.AddRange(FindMessageReferences(fromClass, fromContainer, newFunction.Method));
                            continue;
                        }
                    case OPCode.GetProperty:
                        {
                            var getProperty = (GetPropertyIns)instruction;
                            nameStack.Push(getProperty.PropertyName);
                            continue;
                        }
                    case OPCode.GetLex:
                        {
                            var getLex = (GetLexIns)instruction;
                            container = abc.GetClass(getLex.TypeName.Name);
                            continue;
                        }
                    case OPCode.GetLocal_0:
                        {
                            container = fromContainer;
                            continue;
                        }
                    case OPCode.ConstructProp:
                        {
                            var constructProp = (ConstructPropIns)instruction;

                            extraNamePopCount = constructProp.ArgCount;
                            nameStack.Push(constructProp.PropertyName);
                            break;
                        }
                }

                ASMultiname messageQName = nameStack.Pop();
                if (string.IsNullOrWhiteSpace(messageQName.Name)) continue;

                ASClass messageClass = abc.GetClass(messageQName.Name);
                if (messageClass == null) continue;

                if (!_messages.TryGetValue(messageClass, out var message)) continue;
                if(message.References.Any(r => r.FromMethod == fromMethod)) continue;

                var reference = new HReference();
                message.References.Add(reference);

                if (message.IsOutgoing)
                {
                    reference.FromClass = fromClass;
                    reference.FromMethod = fromMethod;
                    reference.InstructionRank = ++instructionRank;
                    reference.IsAnonymous = (!fromMethod.IsConstructor && fromMethod.Trait == null);

                    references.Add(reference);
                }
                else
                {
                    ASMultiname methodName = nameStack.Pop();
                    ASMethod callbackMethod = fromContainer.GetMethod(methodName.Name);
                    if (callbackMethod == null)
                    {
                        callbackMethod = container.GetMethod(methodName.Name);
                        if (callbackMethod == null)
                        {
                            ASMultiname slotName = nameStack.Pop();

                            ASTrait hostTrait = container.GetTraits(TraitKind.Slot)
                                .FirstOrDefault(st => st.QName == slotName);

                            container = abc.GetInstance(hostTrait.Type.Name);
                            callbackMethod = container.GetMethod(methodName.Name);
                        }
                    }
                    reference.FromMethod = callbackMethod;
                }
            }
            return references;
        }
        #endregion

        private void LoadMessages()
        {
            ABCFile abc = ABCFiles.Last();
            ASClass habboMessagesClass = abc.GetClass("HabboMessages");
            if (habboMessagesClass == null)
            {
                IsPostShuffle = false;
                foreach (ASClass @class in abc.Classes)
                {
                    if (@class.Traits.Count != 2 || @class.Traits[0].Type?.Name != "Map" || @class.Traits[1].Type?.Name != "Map") continue;
                    if (@class.Instance.Traits.Count != 2) continue;

                    habboMessagesClass = @class;
                    break;
                }
                if (habboMessagesClass == null) return;
            }

            ASCode code = habboMessagesClass.Constructor.Body.ParseCode();
            int inMapTypeIndex = habboMessagesClass.Traits[0].QNameIndex;
            int outMapTypeIndex = habboMessagesClass.Traits[1].QNameIndex;

            ASInstruction[] instructions = code
                .Where(i => i.OP == OPCode.GetLex ||
                            i.OP == OPCode.PushShort ||
                            i.OP == OPCode.PushByte)
                .ToArray();

            for (int i = 0; i < instructions.Length; i += 3)
            {
                var getLexInst = (instructions[i + 0] as GetLexIns);
                bool isOutgoing = (getLexInst.TypeNameIndex == outMapTypeIndex);

                var primitive = (instructions[i + 1] as Primitive);
                ushort id = Convert.ToUInt16(primitive.Value);

                getLexInst = (instructions[i + 2] as GetLexIns);
                ASClass messageClass = abc.GetClass(getLexInst.TypeName.Name);

                var message = new HMessage(id, isOutgoing, messageClass);
                (isOutgoing ? OutMessages : InMessages).Add(id, message);

                if (_messages.ContainsKey(messageClass))
                {
                    //_messages[messageClass].SharedIds.Add(id);
                }
                else _messages.Add(messageClass, message);

                if (id == 4000 && isOutgoing)
                {
                    ASInstance messageInstance = messageClass.Instance;
                    ASMethod toArrayMethod = messageInstance.GetMethod(null, "Array", 0);

                    ASCode toArrayCode = toArrayMethod.Body.ParseCode();
                    int index = toArrayCode.IndexOf(OPCode.PushString);
                }
            }
        }

        public override void Disassemble(Action<TagItem> callback)
        {
            base.Disassemble(callback);
            LoadMessages();
        }

        protected override void WriteTag(TagItem tag, FlashWriter output)
        {
            if (tag.Kind == TagKind.DoABC)
            {
                var doABCTag = (DoABCTag)tag;
                doABCTag.ABCData = _abcFileTags[doABCTag].ToArray();
            }
            base.WriteTag(tag, output);
        }

        protected override TagItem ReadTag(HeaderRecord header, FlashReader input)
        {
            TagItem tag = base.ReadTag(header, input);
            if (tag.Kind == TagKind.DoABC)
            {
                var doABCTag = (DoABCTag)tag;
                var abcFile = new ABCFile(doABCTag.ABCData);

                _abcFileTags[doABCTag] = abcFile;
                ABCFiles.Add(abcFile);
            }
            return tag;
        }

        public static bool IsValidIdentifier(string value, bool invalidOnSanitized = false)
        {
            value = value.ToLower();
            if (invalidOnSanitized &&
                (value.StartsWith("class_") ||
                value.StartsWith("iinterface_") ||
                value.StartsWith("namespace_") ||
                value.StartsWith("method_") ||
                value.StartsWith("constant_") ||
                value.StartsWith("slot_") ||
                value.StartsWith("param")))
            {
                return false;
            }

            return (!value.Contains("_-") &&
                !_reservedNames.Contains(value.Trim()));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _messages.Clear();
                _abcFileTags.Clear();
                foreach (ABCFile abc in ABCFiles)
                {
                    abc.Dispose();
                }
                ABCFiles.Clear();
            }
        }
    }

    public class HashWriter : BinaryWriter
    {
        private readonly SortedDictionary<int, int> _ints;
        private readonly SortedDictionary<bool, int> _bools;
        private readonly SortedDictionary<byte, int> _bytes;
        private readonly SortedDictionary<string, int> _strings;

        public bool IsSorting { get; set; }

        public HashWriter(bool isSorting)
            : base(new MemoryStream())
        {
            _ints = new SortedDictionary<int, int>();
            _bools = new SortedDictionary<bool, int>();
            _bytes = new SortedDictionary<byte, int>();
            _strings = new SortedDictionary<string, int>();

            IsSorting = isSorting;
        }

        public override void Write(int value)
        {
            WriteOrSort(_ints, base.Write, value);
        }
        public override void Write(bool value)
        {
            WriteOrSort(_bools, base.Write, value);
        }
        public override void Write(byte value)
        {
            WriteOrSort(_bytes, base.Write, value);
        }
        public override void Write(string value)
        {
            WriteOrSort(_strings, base.Write, value);
        }

        public void Write(ASTrait trait)
        {
            Write(trait.Id);
            Write(trait.QName);
            Write(trait.IsStatic);
            Write((byte)trait.Kind);
            Write((byte)trait.Attributes);
            switch (trait.Kind)
            {
                case TraitKind.Slot:
                case TraitKind.Constant:
                    {
                        Write(trait.Type);
                        if (trait.Value != null)
                        {
                            Write(trait.ValueKind, trait.Value);
                        }
                        break;
                    }
                case TraitKind.Method:
                case TraitKind.Getter:
                case TraitKind.Setter:
                    {
                        Write(trait.Method);
                        break;
                    }
            }
        }
        public void Write(ASMethod method)
        {
            Write(method.IsConstructor);
            if (!method.IsConstructor)
            {
                Write(method.ReturnType);
            }
            Write(method.Parameters.Count);
            foreach (ASParameter parameter in method.Parameters)
            {
                Write(parameter.Type);
                if (!string.IsNullOrWhiteSpace(parameter.Name) &&
                    HGame.IsValidIdentifier(parameter.Name, true))
                {
                    Write(parameter.Name);
                }
                Write(parameter.IsOptional);
                if (parameter.IsOptional)
                {
                    Write((byte)parameter.ValueKind);
                    Write(parameter.ValueKind, parameter.Value);
                }
            }
            ASCode code = method.Body.ParseCode();
            foreach (OPCode op in code.GetOPGroups().Keys)
            {
                if (op != OPCode.GetLex
                    && op != OPCode.GetProperty
                    && op != OPCode.CallProperty) continue;

                Write((byte)op);
            }
        }
        public void Write(ASMultiname multiname)
        {
            if (multiname?.Kind == MultinameKind.TypeName)
            {
                Write(multiname.QName);
                Write(multiname.TypeIndices.Count);
                foreach (ASMultiname type in multiname.GetTypes())
                {
                    Write(type);
                }
            }
            else if (multiname == null ||
                HGame.IsValidIdentifier(multiname.Name, true))
            {
                Write(multiname?.Name ?? "*");
            }
        }
        public void Write(ConstantKind kind, object value)
        {
            Write((byte)kind);
            switch (kind)
            {
                case ConstantKind.Double:
                    Write((double)value);
                    break;
                case ConstantKind.Integer:
                    Write((int)value);
                    break;
                case ConstantKind.UInteger:
                    Write((uint)value);
                    break;
                case ConstantKind.String:
                    Write((string)value);
                    break;
                case ConstantKind.Null:
                    Write("null");
                    break;
                case ConstantKind.True:
                    Write(true);
                    break;
                case ConstantKind.False:
                    Write(false);
                    break;
            }
        }
        public void Write(ASContainer container, bool includeTraits)
        {
            Write(container.IsStatic);
            if (includeTraits)
            {
                Write(container.Traits.Count);
                container.Traits.ForEach(t => Write(t));
            }
        }

        public override void Flush()
        {
            WriteSorted(_ints, base.Write);
            WriteSorted(_bools, base.Write);
            WriteSorted(_bytes, base.Write);
            WriteSorted(_strings, base.Write);
        }
        public string GenerateHash()
        {
            Flush();
            using (var md5 = MD5.Create())
            {
                long curPos = BaseStream.Position;
                BaseStream.Position = 0;

                byte[] hashData = md5.ComputeHash(BaseStream);
                string hashAsHex = (BitConverter.ToString(hashData));

                BaseStream.Position = curPos;
                return hashAsHex.Replace("-", string.Empty).ToLower();
            }
        }

        private void WriteSorted<T>(IDictionary<T, int> storage, Action<T> writer)
        {
            foreach (KeyValuePair<T, int> storedPair in storage)
            {
                writer(storedPair.Key);
                base.Write(storedPair.Value);
            }
        }
        private void WriteOrSort<T>(IDictionary<T, int> storage, Action<T> writer, T value)
        {
            if (IsSorting)
            {
                if (storage.ContainsKey(value))
                {
                    storage[value]++;
                }
                else storage.Add(value, 1);
            }
            else writer(value);
        }
    }

    public class HMessage : IEquatable<HMessage>
    {
        public ushort Id { get; set; }
        public string Hash { get; set; }

        public bool IsOutgoing { get; set; }

        public string Name { get; set; }
        public PacketValue[] Structure { get; set; }

        public ASClass Class { get; set; }
        public string ClassName { get; }

        public ASClass Parser { get; set; }
        public string ParserName { get; }

        public List<HReference> References { get; }

        public static implicit operator ushort(HMessage message) => message?.Id ?? ushort.MaxValue;

        public HMessage()
            : this(ushort.MaxValue, false, null, null, null)
        { }
        public HMessage(ushort id, bool isOutgoing, ASClass messageClass)
        {
            Id = id;
            IsOutgoing = isOutgoing;
            References = new List<HReference>();

            Class = messageClass;
            ClassName = messageClass.QName.Name;

            if (!IsOutgoing)
            {
                Parser = GetMessageParser();
                if (Parser != null)
                {
                    ParserName = Parser.QName.Name;
                    Structure = GetIncomingStructure(Parser);
                }
            }
            else
            {
                Structure = GetOutgoingStructure(Class);
            }
        }
        public HMessage(ushort id, bool isOutgoing, string hash, string name, PacketValue[] structure)
        {
            Id = id;
            Hash = hash;
            Name = name;
            Structure = structure;
            IsOutgoing = isOutgoing;
        }

        public string GenerateHash()
        {
            if (!string.IsNullOrWhiteSpace(Hash))
            {
                return Hash;
            }
            using (var output = new HashWriter(false))
            {
                output.Write(IsOutgoing);
                if (!HGame.IsValidIdentifier(Class.QName.Name, true))
                {
                    output.Write(Class.Instance, true);
                    output.Write(Class.Instance.Constructor);

                    output.Write(References.Count);
                    foreach (HReference reference in References)
                    {
                        output.Write(reference.IsStatic);
                        output.Write(reference.IsAnonymous);

                        output.Write(reference.MethodRank);
                        output.Write(reference.InstructionRank);

                        output.Write(reference.FromMethod);

                        output.Write(reference.FromClass.Constructor);
                        output.Write(reference.FromClass.Instance.Constructor);
                    }
                    if (!IsOutgoing && Parser != null)
                    {
                        output.Write(Parser.Instance, true);
                    }
                }
                else output.Write(Class.QName.Name);
                return Hash = output.GenerateHash();
            }
        }

        public override string ToString() => Id.ToString();
        public bool Equals(HMessage other) => Id == other.Id;

        #region Structure Extraction
        private ASClass GetMessageParser()
        {
            ABCFile abc = Class.GetABC();
            ASInstance instance = Class.Instance;

            ASInstance superInstance = abc.GetInstance(instance.Super);
            if (superInstance == null) superInstance = instance;

            ASMethod parserGetterMethod = superInstance.GetGetter("parser")?.Method;
            if (parserGetterMethod == null) return null;

            IEnumerable<ASMethod> methods = instance.GetMethods();
            foreach (ASMethod method in methods.Concat(new[] { instance.Constructor }))
            {
                ASCode code = method.Body.ParseCode();
                foreach (ASInstruction instruction in code)
                {
                    ASMultiname multiname = null;
                    if (instruction.OP == OPCode.FindPropStrict)
                    {
                        var findPropStrictIns = (FindPropStrictIns)instruction;
                        multiname = findPropStrictIns.PropertyName;
                    }
                    else if (instruction.OP == OPCode.GetLex)
                    {
                        var getLexIns = (GetLexIns)instruction;
                        multiname = getLexIns.TypeName;
                    }
                    else continue;

                    foreach (ASClass refClass in abc.GetClasses(multiname))
                    {
                        ASInstance refInstance = refClass.Instance;
                        if (refInstance.ContainsInterface(parserGetterMethod.ReturnType.Name))
                        {
                            return refClass;
                        }
                    }
                }
            }
            return null;
        }

        private PacketValue[] GetIncomingStructure(ASClass @class)
        {
            ASMethod parseMethod = @class.Instance.GetMethod("parse", "Boolean", 1);
            return GetIncomingStructure(@class.Instance, parseMethod);
        }

        private PacketValue[] GetIncomingStructure(ASInstance instance, ASMethod method)
        {
            if (method.Body.Exceptions.Count > 0) return null;

            ASCode code = method.Body.ParseCode();
            if (code.JumpExits.Count > 0 || code.SwitchExits.Count > 0) return null;

            List<PacketValue> structure = new List<PacketValue>();
            ABCFile abc = method.GetABC();
            for (int i = 0; i < code.Count; i++)
            {
                ASInstruction instruction = code[i];
                if (instruction.OP != OPCode.GetLocal_1) continue;

                ASInstruction next = code[++i];
                switch (next.OP)
                {
                    case OPCode.CallProperty:
                        {
                            var callProperty = (CallPropertyIns)next;
                            if (callProperty.ArgCount > 0)
                            {
                                ASMultiname propertyName = null;
                                ASInstruction previous = code[i - 2];

                                switch (previous.OP)
                                {
                                    case OPCode.GetLex:
                                        {
                                            var getLex = (GetLexIns)previous;
                                            propertyName = getLex.TypeName;
                                            break;
                                        }

                                    case OPCode.ConstructProp:
                                        {
                                            var constructProp = (ConstructPropIns)previous;
                                            propertyName = constructProp.PropertyName;
                                            break;
                                        }

                                    case OPCode.GetLocal_0:
                                        {
                                            propertyName = instance.QName;
                                            break;
                                        }
                                }

                                ASInstance innerInstance = abc.GetInstance(propertyName);
                                ASMethod innerMethod = innerInstance.GetMethod(callProperty.PropertyName.Name, null, callProperty.ArgCount);
                                if (innerMethod == null)
                                {
                                    ASClass innerClass = abc.GetClass(propertyName);
                                    innerMethod = innerClass.GetMethod(callProperty.PropertyName.Name, null, callProperty.ArgCount);
                                }

                                PacketValue[] innerStructure = GetIncomingStructure(innerInstance, innerMethod);
                                if (innerStructure == null) return null;
                                structure.AddRange(innerStructure);
                            }
                            else
                            {
                                if (!TryGetPacketValue(callProperty.PropertyName, null, out PacketValue piece)) return null;
                                structure.Add(piece);
                            }
                            break;
                        }

                    case OPCode.ConstructProp:
                        {
                            var constructProp = (ConstructPropIns)next;
                            ASInstance innerInstance = abc.GetInstance(constructProp.PropertyName);

                            PacketValue[] innerStructure = GetIncomingStructure(innerInstance, innerInstance.Constructor);
                            if (innerStructure == null) return null;
                            structure.AddRange(innerStructure);
                            break;
                        }

                    case OPCode.ConstructSuper:
                        {
                            ASInstance superInstance = abc.GetInstance(instance.Super);

                            PacketValue[] innerStructure = GetIncomingStructure(superInstance, superInstance.Constructor);
                            if (innerStructure == null) return null;
                            structure.AddRange(innerStructure);
                            break;
                        }

                    case OPCode.CallSuper:
                        {
                            var callSuper = (CallSuperIns)next;
                            ASInstance superInstance = abc.GetInstance(instance.Super);
                            ASMethod superMethod = superInstance.GetMethod(callSuper.MethodName.Name, null, callSuper.ArgCount);

                            PacketValue[] innerStructure = GetIncomingStructure(superInstance, superMethod);
                            if (innerStructure == null) return null;
                            structure.AddRange(innerStructure);
                            break;
                        }

                    case OPCode.CallPropVoid:
                        {
                            var callPropVoid = (CallPropVoidIns)next;
                            if (callPropVoid.ArgCount != 0) return null;

                            if (!TryGetPacketValue(callPropVoid.PropertyName, null, out PacketValue piece)) return null;
                            structure.Add(piece);
                            break;
                        }

                    default: return null;
                }
            }
            return structure.Count == 0 ? null : structure.ToArray();
        }

        private PacketValue[] GetOutgoingStructure(ASClass @class)
        {
            ASMethod getArrayMethod = @class.Instance.GetMethod(null, "Array", 0);
            if (getArrayMethod == null)
            {
                ASClass superClass = @class.GetABC().GetClass(@class.Instance.Super);
                return GetOutgoingStructure(superClass);
            }
            if (getArrayMethod.Body.Exceptions.Count > 0) return null;
            ASCode getArrayCode = getArrayMethod.Body.ParseCode();

            if (getArrayCode.JumpExits.Count > 0 || getArrayCode.SwitchExits.Count > 0)
            {
                // Unable to parse data structure that relies on user input that is not present,
                // since the structure may change based on the provided input.
                return null;
            }

            ASInstruction resultPusher = null;
            for (int i = getArrayCode.Count - 1; i >= 0; i--)
            {
                ASInstruction instruction = getArrayCode[i];
                if (instruction.OP == OPCode.ReturnValue)
                {
                    resultPusher = getArrayCode[i - 1];
                    break;
                }
            }

            int argCount = -1;
            if (resultPusher.OP == OPCode.ConstructProp)
            {
                argCount = ((ConstructPropIns)resultPusher).ArgCount;
            }
            else if (resultPusher.OP == OPCode.NewArray)
            {
                argCount = ((NewArrayIns)resultPusher).ArgCount;
            }

            if (argCount > 0)
            {
                return GetOutgoingStructure(getArrayCode, resultPusher, argCount);
            }
            else if (argCount == 0 || resultPusher.OP == OPCode.PushNull)
            {
                return null;
            }

            if (resultPusher.OP == OPCode.GetProperty)
            {
                var getProperty = (GetPropertyIns)resultPusher;
                return GetOutgoingStructure(Class, getProperty.PropertyName);
            }
            else if (Local.IsGetLocal(resultPusher.OP))
            {
                return GetOutgoingStructure(getArrayCode, (Local)resultPusher);
            }

            return null;
        }

        private PacketValue[] GetOutgoingStructure(ASCode code, Local getLocal)
        {
            List<PacketValue> structure = new List<PacketValue>();
            for (int i = 0; i < code.Count; i++)
            {
                ASInstruction instruction = code[i];
                if (instruction == getLocal) break;
                if (!Local.IsGetLocal(instruction.OP)) continue;

                var local = (Local)instruction;
                if (local.Register != getLocal.Register) continue;

                for (i += 1; i < code.Count; i++)
                {
                    ASInstruction next = code[i];
                    if (next.OP != OPCode.CallPropVoid) continue;

                    var callPropVoid = (CallPropVoidIns)next;
                    if (callPropVoid.PropertyName.Name != "push") continue;

                    ASInstruction previous = code[i - 1];
                    if (previous.OP == OPCode.GetProperty)
                    {
                        ASClass classToCheck = Class;
                        var getProperty = (GetPropertyIns)previous;
                        ASMultiname propertyName = getProperty.PropertyName;

                        ASInstruction beforeGetProp = code[i - 2];
                        if (beforeGetProp.OP == OPCode.GetLex)
                        {
                            var getLex = (GetLexIns)beforeGetProp;
                            classToCheck = classToCheck.GetABC().GetClass(getLex.TypeName);
                        }

                        if (!TryGetPacketValue(propertyName, classToCheck, out PacketValue piece)) return null;
                        structure.Add(piece);
                    }
                }
            }
            return structure.Count == 0 ? null : structure.ToArray();
        }

        private PacketValue[] GetOutgoingStructure(ASClass @class, ASMultiname propertyName)
        {
            ASMethod constructor = @class.Instance.Constructor;
            if (constructor.Body.Exceptions.Count > 0) return null;

            ASCode code = constructor.Body.ParseCode();
            if (code.JumpExits.Count > 0 || code.SwitchExits.Count > 0) return null;

            List<PacketValue> structure = new List<PacketValue>();
            for (int i = 0; i < code.Count; i++)
            {
                ASInstruction instruction = code[i];
                if (instruction.OP == OPCode.NewArray)
                {
                    var newArray = (NewArrayIns)instruction;
                    if (newArray.ArgCount > 0)
                    {
                        PacketValue[] structurePieces = new PacketValue[newArray.ArgCount];
                        for (int j = i - 1, length = newArray.ArgCount; j >= 0; j--)
                        {
                            ASInstruction previous = code[j];
                            if (Local.IsGetLocal(previous.OP) && previous.OP != OPCode.GetLocal_0)
                            {
                                var local = (Local)previous;
                                ASParameter parameter = constructor.Parameters[local.Register - 1];

                                if (!TryGetPacketValue(parameter.Type, null, out PacketValue piece)) return null;
                                structurePieces[--length] = piece;
                            }
                            if (length == 0)
                            {
                                structure.AddRange(structurePieces);
                                break;
                            }
                        }
                    }
                }
                else if (instruction.OP == OPCode.ConstructSuper)
                {
                    var constructSuper = (ConstructSuperIns)instruction;
                    if (constructSuper.ArgCount > 0)
                    {
                        ASClass superClass = @class.GetABC().GetClass(@class.Instance.Super);
                        structure.AddRange(GetOutgoingStructure(superClass, propertyName));
                    }
                }
                if (instruction.OP != OPCode.GetProperty) continue;

                var getProperty = (GetPropertyIns)instruction;
                if (getProperty.PropertyName != propertyName) continue;

                ASInstruction next = code[++i];
                ASClass classToCheck = @class;
                if (Local.IsGetLocal(next.OP))
                {
                    if (next.OP == OPCode.GetLocal_0) continue;

                    var local = (Local)next;
                    ASParameter parameter = constructor.Parameters[local.Register - 1];

                    if (!TryGetPacketValue(parameter.Type, null, out PacketValue piece)) return null;
                    structure.Add(piece);
                }
                else
                {
                    if (next.OP == OPCode.FindPropStrict)
                    {
                        classToCheck = null;
                    }
                    else if (next.OP == OPCode.GetLex)
                    {
                        var getLex = (GetLexIns)next;
                        classToCheck = classToCheck.GetABC().GetClass(getLex.TypeName);
                    }
                    do
                    {
                        next = code[++i];
                        propertyName = null;
                        if (next.OP == OPCode.GetProperty)
                        {
                            getProperty = (GetPropertyIns)next;
                            propertyName = getProperty.PropertyName;
                        }
                        else if (next.OP == OPCode.CallProperty)
                        {
                            var callProperty = (CallPropertyIns)next;
                            propertyName = callProperty.PropertyName;
                        }
                    }
                    while (next.OP != OPCode.GetProperty && next.OP != OPCode.CallProperty);

                    if (!TryGetPacketValue(propertyName, classToCheck, out PacketValue piece)) return null;
                    structure.Add(piece);
                }
            }
            return structure.Count == 0 ? null : structure.ToArray();
        }
        private PacketValue[] GetOutgoingStructure(ASCode code, ASInstruction beforeReturn, int length)
        {
            var getLocalEndIndex = -1;
            int pushingEndIndex = code.IndexOf(beforeReturn);

            var structure = new PacketValue[length];
            var pushedLocals = new Dictionary<int, int>();
            for (int i = pushingEndIndex - 1; i >= 0; i--)
            {
                ASInstruction instruction = code[i];
                if (instruction.OP == OPCode.GetProperty)
                {
                    ASClass classToCheck = Class;
                    var getProperty = (GetPropertyIns)instruction;
                    ASMultiname propertyName = getProperty.PropertyName;

                    ASInstruction previous = code[i - 1];
                    if (previous.OP == OPCode.GetLex)
                    {
                        var getLex = (GetLexIns)previous;
                        classToCheck = classToCheck.GetABC().GetClass(getLex.TypeName);
                    }

                    if (!TryGetPacketValue(propertyName, classToCheck, out PacketValue piece)) return null;
                    structure[--length] = piece;
                }
                else if (Local.IsGetLocal(instruction.OP) &&
                    instruction.OP != OPCode.GetLocal_0)
                {
                    var local = (Local)instruction;
                    pushedLocals.Add(local.Register, --length);
                    if (getLocalEndIndex == -1)
                    {
                        getLocalEndIndex = i;
                    }
                }
                if (length == 0) break;
            }
            for (int i = getLocalEndIndex - 1; i >= 0; i--)
            {
                ASInstruction instruction = code[i];
                if (!Local.IsSetLocal(instruction.OP)) continue;

                var local = (Local)instruction;
                if (pushedLocals.TryGetValue(local.Register, out int structIndex))
                {
                    ASInstruction beforeSet = code[i - 1];
                    pushedLocals.Remove(local.Register);
                    switch (beforeSet.OP)
                    {
                        case OPCode.PushInt:
                        case OPCode.PushByte:
                        case OPCode.Convert_i:
                            structure[structIndex] = PacketValue.Integer;
                            break;

                        case OPCode.Coerce_s:
                        case OPCode.PushString:
                            structure[structIndex] = PacketValue.String;
                            break;

                        case OPCode.PushTrue:
                        case OPCode.PushFalse:
                            structure[structIndex] = PacketValue.Boolean;
                            break;

                        default:
                            throw new Exception($"Don't know what this value type is, tell someone about this please.\r\nOP: {beforeSet.OP}");
                    }
                }
                if (pushedLocals.Count == 0) break;
            }
            return structure;
        }

        private ASMultiname GetTraitType(ASContainer container, ASMultiname traitName)
        {
            if (container == null) return traitName;

            return container.GetTraits(TraitKind.Slot, TraitKind.Constant, TraitKind.Getter)
                .Where(t => t.QName == traitName)
                .FirstOrDefault()?.Type;
        }
        private bool TryGetPacketValue(ASMultiname multiname, ASClass @class, out PacketValue value)
        {
            ASMultiname returnValueType = multiname;
            if (@class != null)
            {
                returnValueType = GetTraitType(@class, multiname) ?? GetTraitType(@class.Instance, multiname);
            }

            switch (returnValueType.Name.ToLower())
            {
                case "int":
                case "readint":
                case "gettimer": value = PacketValue.Integer; break;

                case "byte":
                case "readbyte": value = PacketValue.Byte; break;

                case "double":
                case "readdouble": value = PacketValue.Double; break;

                case "string":
                case "readstring": value = PacketValue.String; break;

                case "boolean":
                case "readboolean": value = PacketValue.Boolean; break;

                case "array": value = PacketValue.Unknown; break;
                default:
                    {
                        if (!IsOutgoing && !HGame.IsValidIdentifier(returnValueType.Name, true))
                        {
                            value = PacketValue.Integer; // This reference call is most likely towards 'readInt'
                        }
                        else value = PacketValue.Unknown;
                        break;
                    }
            }
            return value != PacketValue.Unknown;
        }
        #endregion

        public class HReference
        {
            public bool IsStatic { get; set; }
            public bool IsAnonymous { get; set; }

            public int ClassRank { get; set; }
            public int MethodRank { get; set; }
            public int InstructionRank { get; set; }

            public int GroupCount { get; set; }

            public ASClass FromClass { get; set; }
            public ASMethod FromMethod { get; set; }
        }
    }
}