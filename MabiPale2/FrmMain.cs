﻿using MabiPale2.Plugins;
using MabiPale2.Properties;
using MabiPale2.Shared;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MabiPale2
{
	public partial class FrmMain : Form
	{
		private PluginManager pluginManager;

		private IntPtr alissaHWnd;

		private Queue<PalePacket> packetQueue;
		private System.Timers.Timer queueTimer;

		private HashSet<int> recvFilter, sendFilter;
		private Dictionary<int, string> opNames;

		public FrmMain()
		{
			InitializeComponent();

			pluginManager = new PluginManager(this);
			packetQueue = new Queue<PalePacket>();

			queueTimer = new System.Timers.Timer();
			queueTimer.Interval = 250;
			queueTimer.Elapsed += OnQueueTimer;

			recvFilter = new HashSet<int>();
			sendFilter = new HashSet<int>();
			opNames = new Dictionary<int, string>();
		}

		private void FrmMain_Load(object sender, EventArgs e)
		{
			if (Settings.Default.X != -1 && Settings.Default.Y != -1)
				StartPosition = FormStartPosition.Manual;

			if (Settings.Default.X != -1) Left = Settings.Default.X;
			if (Settings.Default.Y != -1) Top = Settings.Default.Y;

			Width = Settings.Default.Width;
			Height = Settings.Default.Height;

			if (Settings.Default.Maximized) WindowState = FormWindowState.Maximized;

			UpdateFilters();
			UpdateOpNames();

			LstPackets.ContextMenu = CtxPacketList;

			pluginManager.Load();
		}

		private void FrmMain_FormClosing(object sender, FormClosingEventArgs e)
		{
			Settings.Default.X = Left;
			Settings.Default.Y = Top;
			Settings.Default.Width = Width;
			Settings.Default.Height = Height;
			Settings.Default.Maximized = (WindowState == FormWindowState.Maximized);
			Settings.Default.Save();

			Disconnect();

			pluginManager.OnEnd();
		}

		/// <summary>
		/// Called when selecting a packet in the list,
		/// shows the packet in the textbox.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void LstPackets_SelectedIndexChanged(object sender, EventArgs e)
		{
			if (LstPackets.SelectedItems.Count == 0)
			{
				TxtPacket.Text = "";
				return;
			}

			lock (LstPackets)
				TxtPacket.Text = ((PalePacket)LstPackets.SelectedItems[0].Tag).ToString();
		}

		/// <summary>
		/// Menu item ?>About, opens About dialog.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnMenuAbout_Click(object sender, EventArgs e)
		{
			new FrmAbout().ShowDialog();
		}

		/// <summary>
		/// Menu item File>Exit, closes form.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnMenuExit_Click(object sender, EventArgs e)
		{
			Close();
		}

		/// <summary>
		/// Open file button, opens a log file.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnOpen_Click(object sender, EventArgs e)
		{
			if (OpenLogDialog.ShowDialog() == DialogResult.Cancel)
				return;

			if (ClearListQuestion() == DialogResult.Cancel)
				return;

			var filePath = OpenLogDialog.FileName;

			if (!File.Exists(filePath))
				return;

			LblCurrentFileName.Text = Path.GetFileName(filePath);

			LoadFile(filePath);
		}

		/// <summary>
		/// Asks user about clearing list, clears if answer is yes.
		/// </summary>
		/// <returns>User's answer, Yes, No, or Cancel.</returns>
		private DialogResult ClearListQuestion()
		{
			if (LstPackets.Items.Count == 0)
				return DialogResult.No;

			var answer = MessageBox.Show("Remove current packet data?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
			if (answer == DialogResult.Yes)
				ClearList();

			return answer;
		}

		/// <summary>
		/// Loads log file and adds packets to list.
		/// </summary>
		/// <param name="path"></param>
		private void LoadFile(string path)
		{
			var newPackets = new List<PalePacket>();

			using (var sr = new StreamReader(path))
			{
				string line;
				while ((line = sr.ReadLine()) != null)
				{
					line = line.Trim();
					var recv = false;

					if (string.IsNullOrWhiteSpace(line) || (!line.StartsWith("Send") && !(recv = line.StartsWith("Recv"))))
						continue;

					var spaceIdx = line.IndexOf(' ');

					var date = DateTime.MinValue;
					if (line[4] == '@')
						date = DateTime.Parse(line.Substring(5, spaceIdx - 5));

					var packetStr = line.Substring(spaceIdx + 1, line.Length - spaceIdx - 1);
					var packetArr = HexTool.ToByteArray(packetStr);
					var packet = new Packet(packetArr, 0);
					var formPacket = new PalePacket(packet, date, recv);

					newPackets.Insert(0, formPacket);
				}
			}

			LstPackets.BeginUpdate();
			lock (LstPackets)
			{
				foreach (var packet in newPackets)
					AddPacketToFormList(packet, false);
			}
			LstPackets.EndUpdate();

			foreach (var packet in newPackets)
			{
				if (packet.Received)
					pluginManager.OnRecv(packet);
				else
					pluginManager.OnSend(packet);
			}
		}

		/// <summary>
		/// Updates packet count status label.
		/// </summary>
		private void UpdateCount()
		{
			StatusStrip.InvokeIfRequired((MethodInvoker)delegate
			{
				LblPacketCount.Text = "Packets: " + LstPackets.Items.Count;
			});
		}

		/// <summary>
		/// Adds packet to list, scrolls down if scroll is true.
		/// </summary>
		/// <param name="palePacket"></param>
		/// <param name="scroll"></param>
		private void AddPacketToFormList(PalePacket palePacket, bool scroll)
		{
			var name = "?";
			lock (opNames)
			{
				if (opNames.ContainsKey(palePacket.Op))
					name = opNames[palePacket.Op];
			}

			var lvi = new ListViewItem((palePacket.Received ? "<" : ">") + palePacket.Op.ToString("X8"));
			lvi.UseItemStyleForSubItems = false;
			lvi.BackColor = palePacket.Received ? Color.FromArgb(0x0033bbff) : Color.FromArgb(0x00ff5522);
			lvi.ForeColor = Color.White;
			lvi.Tag = palePacket;

			lvi.SubItems.Add(palePacket.Id.ToString("X16"));
			lvi.SubItems.Add(name);
			lvi.SubItems.Add(palePacket.Time > DateTime.MinValue ? palePacket.Time.ToString("hh:mm:ss.fff") : "");

			LstPackets.InvokeIfRequired((MethodInvoker)delegate
			{
				LstPackets.Items.Add(lvi);

				if (scroll)
					LstPackets.Items[LstPackets.Items.Count - 1].EnsureVisible();
			});

			UpdateCount();
		}

		/// <summary>
		/// Clear button, clears packet list.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnClear_Click(object sender, EventArgs e)
		{
			ClearList();
		}

		/// <summary>
		/// Clears packet list.
		/// </summary>
		private void ClearList()
		{
			LstPackets.BeginUpdate();
			lock (LstPackets)
				LstPackets.Items.Clear();
			LstPackets.EndUpdate();

			TxtPacket.Text = "";

			UpdateCount();
		}

		/// <summary>
		/// Save log file button, opens save dialog to save all logged packets.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnSave_Click(object sender, EventArgs e)
		{
			SaveLogDialog.FileName = DateTime.Now.ToString("yyyyMMdd_hhmmss"); // 20141214_202659

			if (SaveLogDialog.ShowDialog() == DialogResult.Cancel)
				return;

			try
			{
				using (var stream = SaveLogDialog.OpenFile())
				using (var sw = new StreamWriter(stream))
				{
					lock (LstPackets)
					{
						for (int i = LstPackets.Items.Count - 1; i >= 0; --i)
						{
							var packet = (LstPackets.Items[i].Tag as PalePacket);

							var method = packet.Received ? "Recv" : "Send";
							var time = packet.Time.ToString("hh:mm:ss.fff");
							var packetStr = HexTool.ToString(packet.Packet.GetBuffer());

							sw.WriteLine(method + "@" + time + " " + packetStr);
						}
					}

					LblCurrentFileName.Text = Path.GetFileName(SaveLogDialog.FileName);
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("Failed to save file (" + ex.Message + ").", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		/// <summary>
		/// Enables dropping of files.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void FrmMain_DragEnter(object sender, DragEventArgs e)
		{
			e.Effect = (e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None);
		}

		/// <summary>
		/// Handles file drop.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void FrmMain_DragDrop(object sender, DragEventArgs e)
		{
			if (ClearListQuestion() == DialogResult.Cancel)
				return;

			var fileNames = e.Data.GetData(DataFormats.FileDrop) as string[];
			if (fileNames.Length == 0)
				return;

			LoadFile(fileNames[0]);
		}

		/// <summary>
		/// Settings button, opens settings dialog.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnSettings_Click(object sender, EventArgs e)
		{
			var form = new FrmSettings();
			var result = form.ShowDialog();

			if (result == DialogResult.Cancel)
				return;

			UpdateFilters();
		}

		/// <summary>
		/// Clears filter lists and loads them from settings.
		/// </summary>
		private void UpdateFilters()
		{
			lock (recvFilter)
			{
				recvFilter.Clear();
				ReadStringIntList(Settings.Default.FilterRecv, ref recvFilter);
			}

			lock (sendFilter)
			{
				sendFilter.Clear();
				ReadStringIntList(Settings.Default.FilterSend, ref sendFilter);
			}
		}

		/// <summary>
		/// Reads ops from string (line by line) and adds them to hash set.
		/// </summary>
		/// <param name="list"></param>
		/// <param name="set"></param>
		private void ReadStringIntList(string list, ref HashSet<int> set)
		{
			using (var sr = new StringReader(list))
			{
				var line = "";
				while ((line = sr.ReadLine()) != null)
				{
					line = line.Trim().Replace("0x", "");

					int op;
					if (int.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out op))
						set.Add(op);
				}
			}
		}

		/// <summary>
		/// Clears op names list and loads them from settings.
		/// </summary>
		private void UpdateOpNames()
		{
			if (!File.Exists("ops.txt"))
				return;

			try
			{
				lock (opNames)
				{
					opNames.Clear();

					using (var sr = new StreamReader("ops.txt"))
					{
						var line = "";
						while ((line = sr.ReadLine()) != null)
						{
							if (line.TrimStart().StartsWith("//"))
								continue;

							var split = line.Split('=');
							if (split.Length < 2)
								continue;

							var opStr = split[1].Trim().Replace("0x", "");
							var name = split[0].Trim();

							int op;
							if (!int.TryParse(opStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out op))
								continue;

							opNames[op] = name;
						}
					}
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("Failed to load op name list (" + ex.Message + ").", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		/// <summary>
		/// Connect button, sends connect message to Alissa window.
		/// </summary>
		private void BtnConnect_Click(object sender, EventArgs e)
		{
			alissaHWnd = WinApi.FindWindow("TFormAlissa", "mod_Alissa");
			if (alissaHWnd == IntPtr.Zero)
			{
				MessageBox.Show("Failed to connect, Alissa window not found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}

			SendAlissa(alissaHWnd, Sign.Connect);

			BtnConnect.Enabled = false;
			BtnDisconnect.Enabled = true;

			queueTimer.Enabled = true;
		}

		/// <summary>
		/// Disonnect button, sends disconnect message to Alissa window.
		/// </summary>
		private void BtnDisconnect_Click(object sender, EventArgs e)
		{
			Disconnect();
		}

		/// <summary>
		/// Sends disconnect message to Alissa window.
		/// </summary>
		private void Disconnect()
		{
			if (alissaHWnd != IntPtr.Zero)
				SendAlissa(alissaHWnd, Sign.Disconnect);

			this.InvokeIfRequired((MethodInvoker)delegate
			{
				BtnConnect.Enabled = true;
				BtnDisconnect.Enabled = false;
			});

			queueTimer.Enabled = false;
		}

		/// <summary>
		/// Sends message to Alissa window.
		/// </summary>
		/// <param name="hWnd"></param>
		/// <param name="op"></param>
		private void SendAlissa(IntPtr hWnd, int op)
		{
			WinApi.COPYDATASTRUCT cds;
			cds.dwData = (IntPtr)op;
			cds.cbData = 0;
			cds.lpData = IntPtr.Zero;

			var cdsBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(cds));
			Marshal.StructureToPtr(cds, cdsBuffer, false);

			this.InvokeIfRequired((MethodInvoker)delegate
			{
				WinApi.SendMessage(hWnd, WinApi.WM_COPYDATA, this.Handle, cdsBuffer);
			});
		}

		/// <summary>
		/// Window message handler, handles incoming data from Alissa.
		/// </summary>
		/// <param name="m"></param>
		protected override void WndProc(ref Message m)
		{
			if (m.Msg == WinApi.WM_COPYDATA)
			{
				var cds = (WinApi.COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(WinApi.COPYDATASTRUCT));

				if (cds.cbData < 12)
					return;

				var recv = (int)cds.dwData == Sign.Recv;

				var data = new byte[cds.cbData];
				Marshal.Copy(cds.lpData, data, 0, cds.cbData);

				var packet = new Packet(data, 0);
				var palePacket = new PalePacket(packet, DateTime.Now, recv);

				lock (packetQueue)
					packetQueue.Enqueue(palePacket);
			}
			base.WndProc(ref m);
		}

		/// <summary>
		/// Returns a thread-safe list of all current packets.
		/// </summary>
		/// <returns></returns>
		public PalePacket[] GetPacketList()
		{
			lock (LstPackets)
				return LstPackets.Items.Cast<ListViewItem>().Select(a => (PalePacket)a.Tag).ToArray();
		}

		/// <summary>
		/// Fired regularly while being connected, handles queued packets.
		/// </summary>
		/// <param name="state"></param>
		private void OnQueueTimer(object state, EventArgs args)
		{
			if (!WinApi.IsWindow(alissaHWnd))
				Disconnect();

			var count = packetQueue.Count;
			if (count == 0)
				return;

			queueTimer.Enabled = false;

			var newPackets = new List<PalePacket>();
			for (int i = 0; i < count; ++i)
			{
				PalePacket palePacket;
				lock (packetQueue)
					palePacket = packetQueue.Dequeue();

				if (palePacket == null)
					continue;

				newPackets.Add(palePacket);
			}

			foreach (var packet in newPackets)
			{
				lock (recvFilter)
					if (Settings.Default.FilterRecvEnabled && recvFilter.Contains(packet.Op))
						continue;

				lock (sendFilter)
					if (Settings.Default.FilterSendEnabled && sendFilter.Contains(packet.Op))
						continue;

				AddPacketToFormList(packet, true);

				if (packet.Received)
					pluginManager.OnRecv(packet);
				else
					pluginManager.OnSend(packet);
			}

			queueTimer.Enabled = true;
		}

		/// <summary>
		/// Packet list context menu opening, disables invalid items.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void CtxPacketList_Popup(object sender, EventArgs e)
		{
			if (LstPackets.SelectedItems.Count == 0)
			{
				foreach (MenuItem item in CtxPacketList.MenuItems)
					item.Enabled = false;
			}
		}

		/// <summary>
		/// Packet list context menu closing, re-enables all items.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void CtxPacketList_Collapse(object sender, EventArgs e)
		{
			foreach (MenuItem item in CtxPacketList.MenuItems)
				item.Enabled = true;
		}

		/// <summary>
		/// Returns (first) currently selected packet or null.
		/// </summary>
		/// <returns></returns>
		private PalePacket GetSelectedPacket()
		{
			if (LstPackets.SelectedItems.Count == 0)
				return null;

			lock (LstPackets)
				return (PalePacket)LstPackets.SelectedItems[0].Tag;
		}

		/// <summary>
		/// Copies op code of selected packet.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnMenuPacketsCopyOp_Click(object sender, EventArgs e)
		{
			var selected = GetSelectedPacket();
			if (selected == null)
				return;

			Clipboard.SetText(selected.Op.ToString());
		}

		/// <summary>
		/// Copies id of selected packet.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnMenuPacketsCopyId_Click(object sender, EventArgs e)
		{
			var selected = GetSelectedPacket();
			if (selected == null)
				return;

			Clipboard.SetText(selected.Id.ToString());
		}

		/// <summary>
		/// Copies selected packet's buffer as hex string.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnMenuPacketsCopyHex_Click(object sender, EventArgs e)
		{
			var selected = GetSelectedPacket();
			if (selected == null)
				return;

			var str = HexTool.ToString(selected.Packet.GetBuffer());

			Clipboard.SetText(str);
		}

		/// <summary>
		/// Generates code to create the selected packet in Aura.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnMenuPacketsCopyAuraWrite_Click(object sender, EventArgs e)
		{
			var selected = GetSelectedPacket();
			if (selected == null)
				return;

			var sb = new StringBuilder();

			sb.AppendFormat("var gp = new Packet(0x{0:X8}, 0x{1:X16});" + Environment.NewLine, selected.Op, selected.Id);

			PacketElementType peek;
			while ((peek = selected.Packet.Peek()) != PacketElementType.None)
			{
				switch (peek)
				{
					case PacketElementType.Byte: sb.AppendFormat("gp.PutByte({0});" + Environment.NewLine, selected.Packet.GetByte()); break;
					case PacketElementType.Short: sb.AppendFormat("gp.PutShort({0});" + Environment.NewLine, selected.Packet.GetShort()); break;
					case PacketElementType.Int: sb.AppendFormat("gp.PutInt({0});" + Environment.NewLine, selected.Packet.GetInt()); break;
					case PacketElementType.Long: sb.AppendFormat("gp.PutLong({0});" + Environment.NewLine, selected.Packet.GetLong()); break;
					case PacketElementType.Float: sb.AppendFormat("gp.PutFloat({0});" + Environment.NewLine, selected.Packet.GetFloat()); break;
					case PacketElementType.String: sb.AppendFormat("gp.PutString(\"{0}\");" + Environment.NewLine, selected.Packet.GetString()); break;
					case PacketElementType.Bin:
						var bin = selected.Packet.GetBin();

						var binsb = new StringBuilder();
						foreach (var b in bin)
							binsb.Append("0x" + b.ToString("X2") + ", ");

						sb.AppendFormat("gp.PutBin(new byte[] {{ {0} }});" + Environment.NewLine, binsb.ToString().TrimEnd(' ', ','));
						break;
				}
			}

			selected.Packet.Rewind();

			Clipboard.SetText(sb.ToString());
		}

		/// <summary>
		/// Generates code to read the selected packet in Aura.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnMenuPacketsCopyAuraRead_Click(object sender, EventArgs e)
		{
			var selected = GetSelectedPacket();
			if (selected == null)
				return;

			var sb = new StringBuilder();

			int count = 1;

			PacketElementType peek;
			while ((peek = selected.Packet.Peek()) != PacketElementType.None)
			{
				switch (peek)
				{
					case PacketElementType.Byte: selected.Packet.GetByte(); sb.AppendFormat("var var{0} = packet.GetByte();" + Environment.NewLine, count++); break;
					case PacketElementType.Short: selected.Packet.GetShort(); sb.AppendFormat("var var{0} = packet.GetShort();" + Environment.NewLine, count++); break;
					case PacketElementType.Int: selected.Packet.GetInt(); sb.AppendFormat("var var{0} = packet.GetInt();" + Environment.NewLine, count++); break;
					case PacketElementType.Long: selected.Packet.GetLong(); sb.AppendFormat("var var{0} = packet.GetLong();" + Environment.NewLine, count++); break;
					case PacketElementType.Float: selected.Packet.GetFloat(); sb.AppendFormat("var var{0} = packet.GetFloat();" + Environment.NewLine, count++); break;
					case PacketElementType.String: selected.Packet.GetString(); sb.AppendFormat("var var{0} = packet.GetString();" + Environment.NewLine, count++); break;
					case PacketElementType.Bin: selected.Packet.GetBin(); sb.AppendFormat("var var{0} = packet.GetBin();" + Environment.NewLine, count++); break;
				}
			}

			selected.Packet.Rewind();

			Clipboard.SetText(sb.ToString());
		}

		/// <summary>
		/// Adds the selected packet's op to the filter list.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnMenuPacketsFilter_Click(object sender, EventArgs e)
		{
			var selected = GetSelectedPacket();
			if (selected == null)
				return;

			var result = MessageBox.Show("Remove packets with this op from list now?", Text, MessageBoxButtons.YesNoCancel);
			if (result == DialogResult.Cancel)
				return;

			var addition = Environment.NewLine + selected.Op.ToString("X8") + Environment.NewLine;

			if (selected.Received)
				Settings.Default.FilterRecv += addition;
			else
				Settings.Default.FilterSend += addition;

			Settings.Default.Save();

			UpdateFilters();

			if (result == DialogResult.Yes)
				FilterPacketList(selected.Op, selected.Received);
		}

		/// <summary>
		/// Removes specific packets from the packet list.
		/// </summary>
		private void FilterPacketList(int op, bool received)
		{
			var toRemove = new List<int>();

			lock (LstPackets)
			{
				for (int i = 0; i < LstPackets.Items.Count; ++i)
				{
					var packet = (PalePacket)LstPackets.Items[i].Tag;
					if (packet.Op == op && (!received || (received && packet.Received)))
						toRemove.Add(i);
				}
			}

			RemoveFromList(toRemove);
		}

		/// <summary>
		/// Removes packets at the given indexes from list.
		/// </summary>
		/// <param name="idxs"></param>
		private void RemoveFromList(IList<int> idxs)
		{
			LstPackets.BeginUpdate();
			lock (LstPackets)
			{
				for (int i = idxs.Count - 1; i >= 0; --i)
					LstPackets.Items.RemoveAt(idxs[i]);
			}
			LstPackets.EndUpdate();
		}

		/// <summary>
		/// Filters the packet list using the filter settings.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void BtnMenuEditFilter_Click(object sender, EventArgs e)
		{
			var toRemove = new List<int>();

			lock (LstPackets)
			{
				for (int i = 0; i < LstPackets.Items.Count; ++i)
				{
					var packet = (PalePacket)LstPackets.Items[i].Tag;
					if (packet.Received && Settings.Default.FilterRecvEnabled)
					{
						lock (recvFilter)
						{
							if (recvFilter.Contains(packet.Op))
								toRemove.Add(i);
						}
					}
					else if (!packet.Received && Settings.Default.FilterSendEnabled)
					{
						lock (sendFilter)
						{
							if (sendFilter.Contains(packet.Op))
								toRemove.Add(i);
						}
					}
				}
			}

			RemoveFromList(toRemove);
		}

		/// <summary>
		/// Removes selected packets from list on pressing Delete.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void LstPackets_KeyUp(object sender, KeyEventArgs e)
		{
			if (e.KeyCode != Keys.Delete)
				return;

			var toRemove = new List<int>();

			foreach (ListViewItem item in LstPackets.SelectedItems)
				toRemove.Add(item.Index);

			RemoveFromList(toRemove);
		}

		/// <summary>
		/// Fired when form is shown, calls Ready event for plugins.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void FrmMain_Shown(object sender, EventArgs e)
		{
			pluginManager.OnReady();
		}
	}
}
