﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Net.Mail;
using System.Media;
using System.IO;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using nMail;
using ACryptLib;
using System.Threading.Tasks;
using System.Threading;

namespace AkaneMail
{
    public partial class MainForm : Form
    {
        private static readonly string DataRootPath = Application.StartupPath;

        private static readonly string TempFileRoot = DataRootPath + @"\tmp";

        private static readonly string SettingFilePath = DataRootPath + @"\AkaneMail.xml";

        private static readonly string MailDataPath = DataRootPath + @"\Mail.dat";

        // メールを格納する配列
        MailBox mailBox;

        // ListViewItemSorterに指定するフィールド
        public ListViewItemComparer listViewItemSorter = ListViewItemComparer.Default;

        // 選択された行を格納するフィールド
        private int currentRow;

        #region "flags"
        // データ変更が発生したのときのフラグ
        public bool dataModified;

        // 添付付きメールの返信用文字列
        public string attachMailBody = "";
        #endregion

        // 環境保存用のクラスインスタンス
        private MailSettings MailSetting;

        // 点滅用 Win32API のインポート
        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);
        public static void FlashWindow(Form window)
        {
            FlashWindow(window.Handle, false);
        }

        /// <summary>
        /// ListViewの項目の並び替えに使用するクラス
        /// </summary>
        public class ListViewItemComparer : System.Collections.IComparer
        {
            static ListViewItemComparer()
            {
                ColumnModes = new[] { ComparerMode.String, ComparerMode.String, ComparerMode.DateTime, ComparerMode.Integer };
            }

            /// <summary>
            /// 比較する方法
            /// </summary>
            public enum ComparerMode
            {
                String,
                Integer,
                DateTime
            };

            private int _column;
            /// <summary>
            /// 並び替えるListView列の番号
            /// </summary>
            public int Column
            {
                get { return _column; }
                set
                {
                    if (_column == value) {
                        Order = Order.Invert();
                    }
                    _column = value;
                }
            }

            public static ListViewItemComparer Default { get { return new ListViewItemComparer(2, SortOrder.Descending); } }

            /// <summary>
            /// 昇順か降順か
            /// </summary>
            private SortOrder Order { get; set; }

            /// <summary>
            /// 列ごとの並び替えの方法
            /// </summary>
            private static  ComparerMode[] ColumnModes { get; set; }

            /// <summary>
            /// ListViewItemComparerクラスのコンストラクタ
            /// </summary>
            /// <param name="col">並び替える列番号</param>
            /// <param name="ord">昇順か降順か</param>
            /// <param name="cmod">並び替えの方法</param>
            public ListViewItemComparer(int col, SortOrder ord)
            {
                Column = col;
                Order = ord;
            }

            public ListViewItemComparer() : this(0, SortOrder.Ascending) { }
 
            // xがyより小さいときはマイナスの数、大きいときはプラスの数、
            // 同じときは0を返す
            public int Compare(object x, object y)
            {
                if (ColumnModes == null || ColumnModes.Length <= Column) return 0;

                var result = Compare(((ListViewItem)x).SubItems[Column].Text, ((ListViewItem)y).SubItems[Column].Text);

                // 降順の時は結果を+-逆にする
                if (Order == SortOrder.Descending)
                    result = -result;
                else if (Order == SortOrder.None)
                    result = 0;

                return result;
            }

            private int Compare(string itemx, string itemy)
            {
                switch (ColumnModes[Column]) {
                    case ComparerMode.String:
                        return string.Compare(itemx, itemy);
                    case ComparerMode.Integer:
                        return int.Parse(itemx) - int.Parse(itemy);
                    case ComparerMode.DateTime:
                        return DateTime.Compare(DateTime.Parse(itemx), DateTime.Parse(itemy));
                    default:
                        return 0;
                }
            }
        }

        public MainForm()
        {
            // はじめは最小化した状態にしておく
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;

            InitializeComponent();

            Application.Idle += Application_Idle;

            mailBox = new MailBox();
        }

        /// <summary>
        /// ツリービューの更新
        /// </summary>
        public void UpdateTreeView()
        {
            // メールの件数を設定する
            treeMailBoxFolder.Nodes[0].Nodes[0].Text = mailBox.Receive.ToString();
            treeMailBoxFolder.Nodes[0].Nodes[1].Text = mailBox.Send.ToString();
            treeMailBoxFolder.Nodes[0].Nodes[2].Text = mailBox.Trash.ToString();
        }

        /// <summary>
        /// リストビューの更新
        /// </summary>
        public void UpdateListView()
        {
            if (AccountSelected()) {
                // メールボックスのとき
                InitializeMailBox();
                return;
            }

            listMail.BeginUpdate();
            listMail.Items.Clear();
            var folder = GetSelectedMailFolder(listMail.Columns[0].Text);
            listMail.Items.AddRange(folder.Select(CreateMailItem).ToArray());
            listMail.EndUpdate();
        }

        private bool AccountSelected()
        {
            return listMail.Columns[0].Text == "名前";
        }

        private IEnumerable<Mail> GetSelectedMailFolder(string text)
        {
            if (text == "差出人") {
                // 受信メールの場合
                return mailBox.Receive;
            }
            else if (text == "宛先") {
                // 送信メールの場合
                return mailBox.Send;
            }
            else if (text == "差出人または宛先") {
                // 削除メールの場合
                return mailBox.Trash;
            }
            else return null;
        }

        private void InitializeMailBox()
        {
            listMail.Items.Clear();
            var item = new ListViewItem(AccountInfo.fromName);
            item.SubItems.Add(AccountInfo.mailAddress);
            var fi = new FileInfo(MailDataPath);
            if (fi.Exists) {

                var mailDataDate = fi.LastWriteTime.ToString("yy/MM/dd hh:mm:ss");

                item.SubItems.AddRange(new[] { mailDataDate, fi.Length.ToString() });
            }
            else {
                item.SubItems.AddRange(new[] { "データ未作成", "0" });
            }
            listMail.Items.Add(item);
        }

        private ListViewItem CreateMailItem(Mail mail, int index)
        {
            var item = new ListViewItem(mail.Address)
            {
                Tag = index,
                Name = index.ToString(),
                ForeColor = MailPriority.GetPriorityColor(mail)
            };

            if (mail.Subject != "") {
                item.SubItems.Add(mail.Subject);
            }
            else {
                item.SubItems.Add("(no subject)");
            }

            // メールの受信(送信)日時とメールサイズをリストのサブアイテムに追加する
            item.SubItems.Add(mail.Date);
            item.SubItems.Add(mail.Size);

            // 未読(未送信)の場合は、フォントを太字にする
            if (mail.NotReadYet) {
                item.Font = new Font(this.Font, FontStyle.Bold);
            }

            return item;
        }

        private static object lockobj = new object();
        #region UI更新
        /// <summary>
        /// メール送信・受信用プログレスバーの初期化
        /// </summary>
        private void ProgressMailInit(int value)
        {
            // プログレスバーを表示して最大値を未受信メール件数に設定する
            progressMail.Visible = true;
            progressMail.Minimum = 0;
            progressMail.Maximum = value;
           
        }

        /// <summary>
        /// メール送信・受信件数の更新
        /// </summary>
        private void ProgressMailUpdate(int value)
        {
            // メールの受信件数を更新する
            progressMail.Value = value;
        }

        /// <summary>
        /// メール送信・受信用プログレスバーの非表示
        /// </summary>
        private void HideProgressMail()
        {
            // プログレスバーを非表示にする
            progressMail.Visible = false;
            progressMail.Value = 0;
            progressMail.Minimum = 0;
            progressMail.Maximum = 0;
        }

        /// <summary>
        /// 送受信メニューとツールボタンの更新
        /// </summary>
        private void EnableButton(bool enable)
        {
            // メール受信のメニューとツールボタンを有効化する
            menuRecieveMail.Enabled = true;
            buttonRecieveMail.Enabled = true;

            // メール送信のメニューとツールボタンが有効か設定する
            menuSendMail.Enabled = enable;
            buttonSendMail.Enabled = enable;
        }

        /// <summary>
        /// メール送受信後のTreeView、ListViewの更新
        /// </summary>
        private void UpdateView(ListViewItemComparer sorter = null)
        {
            listMail.ListViewItemSorter = null;

            UpdateTreeView();
            UpdateListView();

            listMail.ListViewItemSorter = sorter ?? listViewItemSorter;
        }

        private void UpdateViewFully()
        {
            // 本文ペインをリセットする
            this.textBody.Text = "";
            if (this.browserBody.Visible) {
                this.browserBody.Visible = false;
                this.textBody.Visible = true;
            }

            UpdateView(ListViewItemComparer.Default);

            // 受信メールのとき
            if (listMail.Columns[0].Text == "差出人") {
                // ListViewの1行目にフォーカスを当て直す
                listMail.Items[0].Selected = true;
                listMail.Items[0].Focused = true;
                listMail.SelectedItems[0].EnsureVisible();
                listMail.Select();
                listMail.Focus();
            }
        }

        private void SetMessage(string message)
        {
            labelMessage.Text = message;
        }

        private void Invoke(Action invokeAction)
        {
            this.Invoke((Delegate)invokeAction);
        }

        private void Invoke<T>(Action<T> action, T param)
        {
            this.Invoke((Delegate)action, param);
        }
        #endregion

        /// <summary>
        /// 設定ファイルからアプリケーション設定を読み出す
        /// </summary>
        public void LoadSettings()
        {
            // 環境設定保存クラスを作成する
            MailSetting = new MailSettings();

            AccountInfo.Reset();

            // 環境設定ファイルが存在する場合は環境設定情報を読み込んでアカウント情報に設定する
            if (File.Exists(SettingFilePath)) {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(MailSettings));
                using (var fs = new FileStream(SettingFilePath, FileMode.Open)) {
                    MailSetting = (MailSettings)serializer.Deserialize(fs);
                }

                #region アカウント情報
                AccountInfo.fromName = MailSetting.m_fromName;
                AccountInfo.mailAddress = MailSetting.m_mailAddress;
                AccountInfo.userName = MailSetting.m_userName;
                AccountInfo.passWord = Decrypt(MailSetting.m_passWord);
                #endregion

                #region 接続情報
                AccountInfo.smtpServer = MailSetting.m_smtpServer;
                AccountInfo.popServer = MailSetting.m_popServer;
                AccountInfo.smtpPortNumber = MailSetting.m_smtpPortNo;
                AccountInfo.popPortNumber = MailSetting.m_popPortNo;
                AccountInfo.apopFlag = MailSetting.m_apopFlag;
                AccountInfo.deleteMail = MailSetting.m_deleteMail;
                AccountInfo.popBeforeSMTP = MailSetting.m_popBeforeSMTP;
                AccountInfo.popOverSSL = MailSetting.m_popOverSSL;
                AccountInfo.smtpAuth = MailSetting.m_smtpAuth;
                #endregion

                #region 自動受信設定
                AccountInfo.autoMailFlag = MailSetting.m_autoMailFlag;
                AccountInfo.getMailInterval = MailSetting.m_getMailInterval;
                #endregion

                #region 通知設定
                AccountInfo.popSoundFlag = MailSetting.m_popSoundFlag;
                AccountInfo.popSoundName = MailSetting.m_popSoundName;
                AccountInfo.bodyIEShow = MailSetting.m_bodyIEShow;
                AccountInfo.minimizeTaskTray = MailSetting.m_minimizeTaskTray;
                #endregion

                #region 画面設定
                // 画面の表示が通常のとき 
                if (MailSetting.m_windowStat == FormWindowState.Normal) {
                    // 過去のバージョンから環境設定ファイルを流用した初期起動以外はこの中に入る
                    if (MailSetting.m_windowLeft != 0 && MailSetting.m_windowTop != 0 && MailSetting.m_windowWidth != 0 && MailSetting.m_windowHeight != 0) {
                        this.Left = MailSetting.m_windowLeft;
                        this.Top = MailSetting.m_windowTop;
                        this.Width = MailSetting.m_windowWidth;
                        this.Height = MailSetting.m_windowHeight;
                    }
                }
                this.WindowState = MailSetting.m_windowStat;
                #endregion
            }
        }

        /// <summary>
        /// アプリケーション設定を設定ファイルに書き出す
        /// </summary>
        public void SaveSettings()
        {
            MailSetting = new MailSettings()
            {
                #region アカウント情報
                m_fromName = AccountInfo.fromName,
                m_mailAddress = AccountInfo.mailAddress,
                m_userName = AccountInfo.userName,
                m_passWord = Encrypt(AccountInfo.passWord),
                #endregion

                #region 接続情報
                m_smtpServer = AccountInfo.smtpServer,
                m_popServer = AccountInfo.popServer,
                m_smtpPortNo = AccountInfo.smtpPortNumber,
                m_popPortNo = AccountInfo.popPortNumber,
                m_apopFlag = AccountInfo.apopFlag,
                m_deleteMail = AccountInfo.deleteMail,
                m_popBeforeSMTP = AccountInfo.popBeforeSMTP,
                m_popOverSSL = AccountInfo.popOverSSL,
                m_smtpAuth = AccountInfo.smtpAuth,
                #endregion

                #region 自動受信設定
                m_autoMailFlag = AccountInfo.autoMailFlag,
                m_getMailInterval = AccountInfo.getMailInterval,
                #endregion

                #region 通知設定
                m_popSoundFlag = AccountInfo.popSoundFlag,
                m_popSoundName = AccountInfo.popSoundName,
                m_bodyIEShow = AccountInfo.bodyIEShow,
                m_minimizeTaskTray = AccountInfo.minimizeTaskTray,
                #endregion

                #region ウィンドウ設定
                m_windowLeft = this.Left,
                m_windowTop = this.Top,
                m_windowWidth = this.Width,
                m_windowHeight = this.Height,
                m_windowStat = this.WindowState
                #endregion
            };

            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(MailSettings));

            using (var fs = new FileStream(SettingFilePath, FileMode.Create)) {
                serializer.Serialize(fs, MailSetting);
            }
        }

        /// <summary>文字列を暗号化します</summary>
        /// <remarks>例外発生時は旧バージョンと同じ動作をします</remarks>
        private string Encrypt(string password)
        {
            try {
                return ACrypt.EncryptPasswordString(password);
            }
            catch (Exception) {
                return password;
            }
        }

        /// <summary>文字列を復号します</summary>
        /// <remarks>例外発生時は旧バージョンと同じ動作をします</remarks>
        private string Decrypt(string password)
        {
            try {
                return ACrypt.DecryptPasswordString(password);
            }
            catch (Exception) {
                return password;
            }
        }

        /// <summary> 添付ファイルメニューに登録されている要素を破棄する </summary>
        private void ClearAttachMenu()
        {
            buttonAttachList.DropDownItems.Clear();
            buttonAttachList.Visible = false;
            attachMailBody = "";
        }

        /// <summary>
        /// デコード機能を使用するかを設定
        /// </summary>
        /// <param name="convert"></param>
        private void ChangeConvertMode(string convert)
        {
            // 変換フラグがない時はHTML/Base64のデコードを有効にする
            if (string.IsNullOrWhiteSpace(convert)) {
                Options.EnableDecodeBody();
            }
            else {
                Options.DisableDecodeBodyText();
            }
        }

        /// <summary>
        /// 指定されたメールを開く
        /// </summary>
        /// <param name="mail">メール</param>
        private void OpenMail(Mail mail)
        {
            ClearAttachMenu();

            var attach = new nMail.Attachment();

            // 保存パスはプログラム直下に作成したtmpに設定する
            attach.Path = TempFileRoot;

            // Contents-Typeがtext/htmlのメールか確認するフラグを取得する
            var isHtmlMail = attach.GetHeaderField("Content-Type:", mail.Header).Contains("text/html");
            var hasAttachments = attach.GetId(mail.Header) != nMail.Attachment.NoAttachmentFile;

            if (hasAttachments || isHtmlMail) {
                try {
                    ChangeConvertMode(mail.Convert);

                    // ヘッダと本文付きの文字列を添付クラスに追加する
                    attach.Add(mail.Header, mail.Body);

                    // 添付ファイルを取り外す
                    attach.Save();
                }
                catch (Exception ex) {
                    labelMessage.Text = String.Format("エラー メッセージ:{0:s}", ex.Message);
                    return;
                }

                // IE コンポーネントを使用かつ HTML パートを保存したファイルがある場合
                if (AccountInfo.bodyIEShow && attach.HtmlFile != "") {
                    // 本文表示用のテキストボックスの表示を非表示にしてHTML表示用のWebBrowserを表示する
                    this.textBody.Visible = false;
                    this.browserBody.Visible = true;

                    // Contents-Typeがtext/htmlでないとき(テキストとHTMLパートが存在する添付メール)
                    if (!isHtmlMail) {
                        // テキストパートを返信文に格納する
                        attachMailBody = attach.Body;
                    }
                    else {
                        // 本文にHTMLタグが直書きされているタイプのHTMLメールのとき
                        // 展開したHTMLファイルをストリーム読み込みしてテキストを返信用の変数に格納する
                        using (var sr = new StreamReader(TempFileRoot + "\\" + attach.HtmlFile, Encoding.Default)) {
                            string htmlBody = sr.ReadToEnd();

                            // HTMLからタグを取り除いた本文を返信文に格納する
                            attachMailBody = Mail.HtmlToText(htmlBody, mail.Header);
                        }
                    }

                    // 添付ファイル保存フォルダに展開されたHTMLファイルをWebBrowserで表示する
                    browserBody.AllowNavigation = true;
                    browserBody.Navigate(attach.Path + @"\" + attach.HtmlFile);
                }
                else {
                    // 添付ファイルを外した本文をテキストボックスに表示する
                    this.browserBody.Visible = false;
                    this.textBody.Visible = true;
                    // IE コンポーネントを使用せず、HTML メールで HTML パートを保存したファイルがある場合
                    if (isHtmlMail && !AccountInfo.bodyIEShow && attach.HtmlFile != "") {
                        // 本文にHTMLタグが直書きされているタイプのHTMLメールのとき
                        // 展開したHTMLファイルをストリーム読み込みしてテキストボックスに表示する
                        using (var sr = new StreamReader(TempFileRoot + "\\" + attach.HtmlFile, Encoding.Default)) {
                            string htmlBody = sr.ReadToEnd();

                            // HTMLからタグを取り除く
                            htmlBody = Mail.HtmlToText(htmlBody, mail.Header);

                            attachMailBody = htmlBody;
                            this.textBody.Text = htmlBody;
                        }
                    }
                    else if (attach.Body != "") {
                        var text = BreakLine(attach.Body);
                        attachMailBody = text;
                        this.textBody.Text = text;
                    }
                    else { 
                        this.textBody.Text = mail.Body;
                    }
                }
                if (attach.FileNameList != null) {
                    // IE コンポーネントありで、添付ファイルが HTML パートを保存したファイルのみの場合はメニューを表示しない
                    if (!AccountInfo.bodyIEShow || attach.HtmlFile == "" || attach.FileNameList.Length > 1) {
                        buttonAttachList.Visible = true;
                        hasAttachments = true;
                        // IE コンポーネントありで、添付ファイルが HTML パートを保存したファイルはメニューに表示しない
                        // foreach (var attachFile in attach.FileNameList.Where(a => a != attach.HtmlFile)) {
                        buttonAttachList.DropDownItems.AddRange(attach.GenerateMenuItem().ToArray());
                    }
                }
            }
            else {
                // 添付ファイルが存在しない通常のメールまたは
                // 送信済みメールのときは本文をテキストボックスに表示する
                this.browserBody.Visible = false;
                this.textBody.Visible = true;

                if (mail.Attaches.Length != 0) {
                    buttonAttachList.Visible = true;

                    buttonAttachList.DropDownItems.AddRange(mail.GenerateMenuItem().ToArray());
                }

                var base64Mail = attach.GetDecodeHeaderField("Content-Transfer-Encoding:", mail.Header).Contains("base64");

                if (base64Mail) {
                    Options.EnableDecodeBody();

                    // ヘッダと本文付きの文字列を添付クラスに追加する
                    attach.Add(mail.Header, mail.Body);

                    // 添付ファイルを取り外す
                    attach.Save();

                    var text = BreakLine(attach.Body);
                    attachMailBody = text;
                    this.textBody.Text = text;
                }
                else {
                    // ISO-2022-JPでかつquoted-printableがある場合(nMail.dllが対応するまでの暫定処理)
                    if (attach.GetHeaderField("Content-Type:", mail.Header).ToLower().Contains("iso-2022-jp") && attach.GetHeaderField("Content-Transfer-Encoding:", mail.Header).Contains("quoted-printable")) {
                        // 文章をデコードする
                        Options.EnableDecodeBody();

                        attach.Add(mail.Header, mail.Body);
                        attach.Save();

                        var text = BreakLine(attach.Body);
                        attachMailBody = text;
                        this.textBody.Text = text;
                    }
                    else if (attach.GetHeaderField("X-NMAIL-BODY-UTF8:", mail.Header).Contains("8bit")) {
                        // Unicode化されたUTF-8文字列をデコードする
                        var bs = mail.Body.Select(c => (byte)c).ToArray();

                        attachMailBody = Encoding.UTF8.GetString(bs);
                        this.textBody.Text = attachMailBody;
                    }
                    else {
                        // テキストボックスに出力する文字コードをJISに変更する
                        var b = Encoding.GetEncoding("iso-2022-jp").GetBytes(mail.Body);
                        string strBody = Encoding.GetEncoding("iso-2022-jp").GetString(b);

                        // 本文をテキストとして表示する
                        this.textBody.Text = strBody;
                    }
                }
            }
        }

        private string BreakLine(string text)
        {
            if (text.Contains("\n\n")) {
                text = text.Replace("\n\n", "\r\n").Replace("\n", "\r\n");
            }
            return text;
        }

        /// <summary>
        /// 返信メールを作成
        /// </summary>
        /// <param name="mail"></param>
        private void CreateReturnMail(Mail mail)
        {
            MailEditorForm NewMailForm = new MailEditorForm();

            // 親フォームをForm1に設定する
            NewMailForm.Owner = this;

            // 送信箱の配列をForm3に渡す
            NewMailForm.SendList = mailBox.Send.ToList();

            // 返信のための宛先・件名を設定する
            NewMailForm.textAddress.Text = mail.Address;
            NewMailForm.textSubject.Text = "Re:" + mail.Subject;

            // UTF-8でエンコードされたメールのときはattachMailBodyを渡す
            if (attachMailBody != "")
            {
                NewMailForm.textBody.Text = "\r\n\r\n---" + mail.Address + " was wrote ---\r\n\r\n" + attachMailBody;
            }
            else
            {
                NewMailForm.textBody.Text = "\r\n\r\n---" + mail.Address + " was wrote ---\r\n\r\n" + mail.Body;
            }

            // メール新規作成フォームを表示する
            NewMailForm.Show();
        }

        /// <summary>
        /// 転送メールを作成
        /// </summary>
        /// <param name="mail">メール</param>
        private void CreateFowerdMail(Mail mail)
        {
            var NewMailForm = new MailEditorForm();

            // 親フォームをForm1に設定する
            NewMailForm.Owner = this;

            // 送信箱の配列をForm3に渡す
            NewMailForm.SendList = mailBox.Send.ToList();

            // 転送のために件名を設定する(件名は空白にする)
            NewMailForm.textAddress.Text = "";
            NewMailForm.textSubject.Text = "Fw:" + mail.Subject;

            NewMailForm.textBody.Text = BuildForwardingBody(mail);

            // 送信メールで添付ファイルがあるとき
            if (mail.Attaches.Length != 0) {
                // 添付リストメニューを表示
                NewMailForm.buttonAttachList.Visible = true;
                // 添付ファイルの数だけメニューを追加する
                NewMailForm.buttonAttachList.DropDownItems.AddRange(mail.GenerateMenuItem().ToArray());
            }
            else if (this.buttonAttachList.Visible) {
                // 受信メールで添付ファイルがあるとき
                // 添付リストメニューを表示　
                NewMailForm.buttonAttachList.Visible = true;

                // 添付ファイルの数だけメニューを追加する
                var attaches = this.buttonAttachList.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text);
                NewMailForm.buttonAttachList.DropDownItems.AddRange(NmailAttachEx.GenerateMenuItem(Application.StartupPath + @"\tmp\", attaches).ToArray());
            }

            // メール新規作成フォームを表示する
            NewMailForm.Show();
        }

        private string BuildForwardingBody(Mail mail)
        {
            var from = "";
            var to = "";
            var sentAt = "";
            var subject = "";

            var atch = new nMail.Attachment();

            // メールヘッダが存在するとき
            if (mail.Header != "") {
                from = atch.GetHeaderField("From:", mail.Header);
                to = atch.GetHeaderField("To:", mail.Header);
                sentAt = atch.GetHeaderField("Date:", mail.Header);
                subject = atch.GetHeaderField("Subject:", mail.Header);
            }
            else {
                from = AccountInfo.mailAddress;
                to = mail.Address;
                sentAt = mail.Date;
                subject = mail.Subject;
            }

            var builder = new StringBuilder("\r\n\r\n")
                .AppendLine("--- Forwarded by " + AccountInfo.mailAddress + " ---")
                .AppendLine("----------------------- Original Message -----------------------")
                .AppendLine("From: " + from)
                .AppendLine("To: " + to)
                .AppendLine("Date: " + sentAt)
                .AppendLine(" Subject:" + subject)
                .AppendLine("----\r\n");

            if (attachMailBody != "") {
                return builder.Append(attachMailBody).ToString();
            }
            else {
                return builder.Append(mail.Body).ToString();
            }
        }

        private void InitializeMailEditorForm(Mail mail, int tag, MainForm mainForm)
        {
            MailEditorForm EditMailForm = new MailEditorForm {
                Owner = mainForm,
                Text = mail.Subject + " - Akane Mail"
            };

            // 送信箱の配列をForm3に渡す
            EditMailForm.SendList = mailBox.Send.ToList();
            EditMailForm.ListTag = tag;
            EditMailForm.IsEdit = true;

            // 宛先、件名、本文をForm3に渡す
            EditMailForm.textAddress.Text = mail.Address;
            EditMailForm.textCc.Text = mail.Cc;
            EditMailForm.textBcc.Text = mail.Bcc;
            EditMailForm.textSubject.Text = mail.Subject;
            EditMailForm.textBody.Text = mail.Body;

            // 重要度をForm3に渡す
            if (mail.Priority == MailPriority.Urgent) {
                EditMailForm.comboPriority.SelectedIndex = 0;
            }
            else if (mail.Priority == MailPriority.Normal) {
                EditMailForm.comboPriority.SelectedIndex = 1;
            }
            else {
                EditMailForm.comboPriority.SelectedIndex = 2;
            }

            // 添付ファイルが付いているとき
            if (mail.Attaches.Length != 0) {
                // 添付リストメニューを表示
                EditMailForm.buttonAttachList.Visible = true;
                // 添付ファイルの数だけメニューを追加する
                EditMailForm.buttonAttachList.DropDownItems.AddRange(mail.GenerateMenuItem(true).ToArray());
            }

            // メール編集フォームを表示する
            EditMailForm.Show();
        }

        /// <summary>
        /// メールの編集
        /// </summary>
        /// <param name="mail">メール</param>
        /// <param name="item">リストアイテム</param>
        private void EditMail(Mail mail, ListViewItem item)
        {
            // 1番目のカラムの表示が差出人か差出人または宛先のとき
            if (listMail.Columns[0].Text == "差出人" || listMail.Columns[0].Text == "差出人または宛先") {
                mail.NotReadYet = false;

                ReforcusListView(listMail);

                dataModified = true;
            }
            else if (listMail.Columns[0].Text == "宛先") {
                InitializeMailEditorForm(mail, (int)item.Tag, this);
            }
        }

        /// <summary>
        /// メールを削除
        /// </summary>
        private void DeleteMail()
        {
            var firstSelected = listMail.SelectedItems[0].Index;
            var items = listMail.SelectedItems.Cast<ListViewItem>();

            if (listMail.Columns[0].Text == "差出人") {
                // 受信メールのとき
                mailBox.MoveToTrash("Receive", items);
            }
            else if (listMail.Columns[0].Text == "宛先") {
                // 送信メールのとき
                mailBox.MoveToTrash("Send", items);
            }
            else if (listMail.Columns[0].Text == "差出人または宛先") {
                if (MessageBox.Show(MainFormMessages.Check.TrashComplete, "確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation) == DialogResult.OK) {
                    // 削除メールのとき
                    mailBox.TrashCompletely(items);
                }
            }

            ClearInput();

            var after = Math.Min(firstSelected, listMail.Items.Count) - 1;

            // リストが空でないとき
            if (after >= 0) {
                // フォーカスをnSelの行に当てる
                listMail.Items[after].Selected = true;
                listMail.Items[after].Focused = true;
                listMail.SelectedItems[0].EnsureVisible();
                listMail.Select();
                listMail.Focus();
            }
            dataModified = true;
        }


        private IEnumerable<int> QueryUnreadMailUids(nMail.Pop3 pop, IEnumerable<Mail> locals)
        {
            // 古い順に通し番号が振られるので、新しい順に見て既存のメールのUIDがヒットするまでTakeする
            var latestUid = locals.AsParallel().OrderBy(d => DateTime.Parse(d.Date)).Last().Uidl;
            pop.GetUidl(nMail.Pop3.UidlAll);
            return pop.Uidl.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Split(new [] {' '}))
                .Reverse()
                .TakeWhile(s => s[1] != latestUid)
                .Select(s => int.Parse(s[0]));
        }

        /// <summary>
        /// POP3サーバからメールを受信する
        /// </summary>
        private void RecieveMail()
        {
            try {
                Invoke(SetMessage, MainFormMessages.Notification.MailReceiving);

                using (var pop = new nMail.Pop3()) {
                    Options.EnableConnectTimeout();
                    pop.APop = AccountInfo.apopFlag;

                    if (AccountInfo.popOverSSL) {
                        pop.SSL = nMail.Pop3.SSL3;
                    }
                    pop.Connect(AccountInfo.popServer, AccountInfo.popPortNumber);
                    pop.Authenticate(AccountInfo.userName, AccountInfo.passWord);

                    var receivingMailIds = CheckReceivingMails(pop);
                    // 受信していないメールがあったとき
                    if (receivingMailIds.Any()) {
                        // プログレスバーを表示(受信件数/未受信件数)
                        Invoke(ProgressMailInit, receivingMailIds.Count());
                        // HTML/Base64のデコードを無効にする
                        Options.DisableDecodeBodyText();

                        Receive(pop, receivingMailIds);

                        Invoke(HideProgressMail);
                    }
                    NotifyReceive(receivingMailIds.Count());
                    Invoke(UpdateViewFully);
                }
            }
            catch (nMail.nMailException ex) {
                Invoke(SetMessage, MainFormMessages.Error.GeneralErrorMessage(ex.Message, ex.ErrorCode));
            }
            catch (Exception ex) {
                Invoke(SetMessage, MainFormMessages.Error.GeneralErrorMessage(ex.Message));
            }
            finally {
                Invoke(EnableButton, true);
            }

        }

        private IEnumerable<int> CheckReceivingMails(Pop3 pop)
        {
            var countMail = Task.Run(() =>
            {
                var locals = mailBox.Receive.Union(mailBox.Trash);
                return QueryUnreadMailUids(pop, locals);
            });

            if (pop.Count == 0) return new int[] { };

            Invoke(SetMessage, pop.Count + "件のメッセージがサーバ上にあります。");

            return countMail.Result;
        }

        private void Receive(Pop3 pop, IEnumerable<int> counts)
        {
            foreach (var no in counts.Select((num, i) => new {num, i })) {
                Invoke(SetMessage, no.num + "件目のメールを受信しています。");
                pop.GetUidl(no.num);
                pop.GetMail(no.num);

                mailBox.Receive.Add(new Mail(pop, true, ""));

                if (AccountInfo.deleteMail) { pop.Delete(no.num); }

                Invoke(ProgressMailUpdate, no.i);
                Task.Delay(TimeSpan.FromSeconds(1)).Wait();
            }

        }

        private void NotifyReceive(int mailCount)
        {
            if (mailCount > 0) {
                // 通知音の再生(設定してあれば)
                if (AccountInfo.popSoundFlag && !string.IsNullOrWhiteSpace(AccountInfo.popSoundName)) {
                    using (var p = new SoundPlayer(AccountInfo.popSoundName)) { p.Play(); }
                }

                notifyIcon1.BalloonTipText = MainFormMessages.Notification.NewMailReceived(mailCount);
                // 通知の表示(タスクトレイに入っていて自動受信したとき)
                if (this.WindowState == FormWindowState.Minimized && AccountInfo.minimizeTaskTray && AccountInfo.autoMailFlag) {
                    notifyIcon1.BalloonTipIcon = ToolTipIcon.Info;
                    notifyIcon1.BalloonTipTitle = MainFormMessages.Notification.NewMail;
                    notifyIcon1.ShowBalloonTip(300);
                }
                else {
                    Invoke(FlashWindow, this);
                }

                dataModified = true;
            }
            else {
                Invoke(SetMessage, MainFormMessages.Notification.AllReceived);
            }
        }

        private void Preauthenticate()
        {
            if (!AccountInfo.popBeforeSMTP) return;
            using (var pop = new nMail.Pop3()) {
                Options.EnableConnectTimeout();

                pop.APop = AccountInfo.apopFlag;

                if (AccountInfo.popOverSSL) {
                    pop.SSL = nMail.Pop3.SSL3;
                }
                pop.Connect(AccountInfo.popServer, AccountInfo.popPortNumber);
                pop.Authenticate(AccountInfo.userName, AccountInfo.passWord);
            }
        }

        #region SendMail
        /// <summary>
        /// メールを送信する
        /// </summary>
        private void SendMail()
        {
            var draftMails = mailBox.Send.Where(m => m.NotReadYet);
            if (!draftMails.Any()) {
                Invoke(EnableButton, true);
                return;
            }

            SendMail(smtp =>
            {
                foreach (var mail in draftMails.Select((Mail, Index) => new { Index, Mail })) {
                    SendSingleMail(smtp, mail.Mail);
                    Invoke(ProgressMailUpdate, mail.Index + 1);
                    Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                }
                Invoke(HideProgressMail);
            });

            Invoke(() => UpdateView());
        }

        /// <summary>
        /// 直接メール送信
        /// </summary>
        /// <param name="Address">宛先</param>
        /// <param name="cc">CCのアドレス</param>
        /// <param name="bcc">BCCのアドレス</param>
        /// <param name="subject">件名</param>
        /// <param name="body">本文</param>
        /// <param name="attach">添付メールリスト</param>
        /// <param name="priority">重要度</param>
        public void DirectSendMail(string address, string cc, string bcc, string subject, string body, string attach, string priority)
        {
            SendMail(smtp => SendSingleMail(smtp, address, cc, bcc, subject, body, attach, priority));
        }

        /// <summary>
        /// Mailクラスの表すメールを送信します。
        /// </summary>
        /// <param name="smtp"></param>
        /// <param name="mail"></param>
        private void SendSingleMail(Smtp smtp, Mail mail)
        {
            SendSingleMail(smtp, mail.Address, mail.Cc, mail.Bcc, mail.Subject, mail.Body, mail.Attach, mail.Priority);

            mail.Date = DateTime.Now.ToString("yyyy/MM/dd hh:mm:ss");
            mail.NotReadYet = false;
        }

        /// <summary>
        /// 送信先や本文などを指定してメールを送信します。
        /// </summary>
        /// <param name="smtp"></param>
        /// <param name="address"></param>
        /// <param name="cc"></param>
        /// <param name="bcc"></param>
        /// <param name="subject"></param>
        /// <param name="body"></param>
        /// <param name="attach"></param>
        /// <param name="priority"></param>
        private void SendSingleMail(Smtp smtp, string address, string cc, string bcc, string subject, string body, string attach, string priority)
        {
            if (cc != "") { smtp.Cc = cc; }
            if (bcc != "") { smtp.Bcc = bcc; }
            if (attach != "") { smtp.FileName = attach; }
            smtp.Header = string.Format("\r\nPriority: {0}\r\nX-Mailer: Akane Mail Version {1}", priority, Application.ProductVersion);
            smtp.SendMail(address, AccountInfo.FromAddress, subject, body);
        }

        /// <summary>
        /// メールを送信します。
        /// </summary>
        /// <param name="sendMailAciton">SMTPクライアントを引数に取る送信操作</param>
        private void SendMail(Action<Smtp> sendMailAciton)
        {
            try {
               Invoke(SetMessage,MainFormMessages.Notification.MailSending);

                Preauthenticate();

                using (var smtp = new Smtp(AccountInfo.smtpServer)) {
                    smtp.Port = AccountInfo.smtpPortNumber;

                    if (AccountInfo.smtpAuth) {
                        smtp.Connect();
                        smtp.Authenticate(AccountInfo.userName, AccountInfo.passWord, Smtp.AuthPlain | Smtp.AuthCramMd5);
                    }

                    sendMailAciton(smtp);
                }

                Invoke(SetMessage, MainFormMessages.Notification.MailSent);
            }
            catch (nMail.nMailException ex) {
                Invoke(SetMessage, MainFormMessages.Error.GeneralErrorMessage(ex.Message, ex.ErrorCode));
            }
            catch (Exception ex) {
                Invoke(SetMessage,  MainFormMessages.Error.GeneralErrorMessage(ex.Message));
            }
        }
        #endregion

        /// <summary>
        /// 添付ファイル付きメールの展開
        /// </summary>
        /// <param name="mail"></param>
        private void GetAttachMail(Mail mail)
        {
            if (folderBrowserDialog1.ShowDialog() != DialogResult.OK) return;
            try {
                // 添付ファイルクラスを作成する
                nMail.Attachment attach = new nMail.Attachment();

                // 保存パスを設定する
                attach.Path = folderBrowserDialog1.SelectedPath;

                // 添付ファイル展開用のテンポラリファイルを作成する
                var tmpFileName = Path.GetTempFileName();
                using (var writer = new StreamWriter(tmpFileName)) {
                    writer.Write(mail.Header);
                    writer.Write("\r\n");
                    writer.Write(mail.Body);
                }

                // テンポラリファイルを開いて添付ファイルを開く
                using (var reader = new StreamReader(tmpFileName)) {
                    string header = reader.ReadToEnd();
                    // ヘッダと本文付きの文字列を添付クラスに追加する
                    attach.Add(header);
                }
                // 添付ファイルを保存する
                attach.Save();

                MessageBox.Show(MainFormMessages.Notification.InternalSaved(attach.Path, attach.FileName), "添付ファイルの取り出し", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (nMailException nex) {
                string message = "";
                switch (nex.ErrorCode) {
                    case nMail.Attachment.ErrorFileOpen:
                        message = "添付ファイルがオープンできません。";
                        break;
                    case nMail.Attachment.ErrorInvalidNo:
                        message = "分割されたメールの順番が正しくないか、該当しないファイルが入っています。";
                        break;
                    case nMail.Attachment.ErrorPartial:
                        message = "分割されたメールが全て揃っていません";
                        break;
                    default:
                        break;
                }
                MessageBox.Show(message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Stop);
            }
            catch (Exception ex) {
                MessageBox.Show(MainFormMessages.Error.GeneralErrorMessage(ex.Message), "エラー", MessageBoxButtons.OK, MessageBoxIcon.Stop);

            }
        }

        /// <summary>
        /// メールファイルの保存
        /// </summary>
        /// <param name="mail">メール</param>
        /// <param name="FileToSave">保存ファイル名</param>
        private void SaveMailFile(Mail mail, string FileToSave)
        {
            // これたぶんバイト列をそのままファイルに突っ込んでいけば幸せになれる気がする
            string fileBody = "";
            string fileHeader = "";

            var encoding = DecideEncoding(mail);
            if (encoding.BodyName == Encoding.UTF8.BodyName) {
                // text/plainまたはmultipart/alternativeでUTF-8でエンコードされたメールのとき
                // nMailの仕様上、UTF-8の文字列が時々化けるのでいったんバイト列にしてからデコードし直す
                var bs = mail.Body.Select(s => (byte)s).ToArray();

                fileBody = Encoding.UTF8.GetString(bs);
                fileHeader = mail.Header;
            }
            else {
                var b = encoding.GetBytes(mail.Header);
                fileHeader = encoding.GetString(b);

                b = encoding.GetBytes(mail.Body);
                fileBody = encoding.GetString(b);
            }

            using (var writer = new StreamWriter(FileToSave, false, encoding)) {
                // 受信メール(ヘッダが存在する)のとき
                if (mail.Header.Length > 0) {
                    writer.Write(fileHeader);
                }
                else {
                    // 送信メールのときはヘッダの代わりに送り先と件名を出力
                    writer.WriteLine("To: " + mail.Address);
                    writer.WriteLine("Subject: " + mail.Subject);
                }
                writer.Write("\r\n");

                writer.Write(fileBody);
            }
        }

        private Encoding DecideEncoding(Mail mail)
        {
            // ヘッダーから文字コードを取得する(添付付きは取得できない)
            string enc = Mail.ParseEncoding(mail.Header) .ToLower();
            if (new [] { "iso-",  "shift_", "euc", "windows", "utf-8"}.Any(enc.Contains)) {
                return  Encoding.GetEncoding(enc);
            }
            else if (mail.Header.Contains("X-NMAIL-BODY-UTF8: 8bit")) {
                return Encoding.UTF8;
            }
            else {
                // 添付ファイルがだいたいここに来る
                return Encoding.GetEncoding("iso-2022-jp");
            }
        }


        /// <summary>
        ///  選択されたメールを取得します。
        /// </summary>
        /// <param name="index">インデックス</param>
        /// <param name="columnText">選択されているカラムの文字列</param>
        /// <returns></returns>
        private Mail GetSelectedMail(object index)
        {
            return GetShowingMailFolder()[(int)index];
        }

        /// <summary>
        /// リストビューのフォーカスをリセットします。
        /// </summary>
        /// <param name="listView">対象のリストビュー</param>
        private void ReforcusListView(ListView listView)
        {
            UpdateView();

            listView.Items[currentRow].Selected = true;
            listView.SelectedItems[0].EnsureVisible();
            listView.Select();
            listView.Focus();
        }

        /// <summary>
        /// 入力欄をクリアします。
        /// </summary>
        private void ClearInput()
        {
            // 本文ペインのリセット
            this.textBody.Text = "";
            if (this.browserBody.Visible) {
                this.browserBody.Visible = false;
                this.textBody.Visible = true;
            }

            // 添付リストのリセット
            if (buttonAttachList.Visible) {
                buttonAttachList.DropDownItems.Clear();
                buttonAttachList.Visible = false;
            }

            // その他のペインのリセット
            UpdateView();
        }

        /// <summary>
        /// リストビューのカラム説明を設定
        /// </summary>
        /// <param name="col1">1カラム目の設定</param>
        /// <param name="col2">2カラム目の設定</param>
        /// <param name="col3">3カラム目の設定</param>
        /// <param name="col4">4カラム目の設定</param>
        private void SetListViewColumns(string col1, string col2, string col3, string col4)
        {
            listMail.Columns[0].Text = col1;
            listMail.Columns[1].Text = col2;
            listMail.Columns[2].Text = col3;
            listMail.Columns[3].Text = col4;
        }

        #region "Event Listeners"
        private void treeMailBoxFolder_AfterSelect(object sender, TreeViewEventArgs e)
        {
            // ノードに付けたタグからリストビューのカラムを変更
            switch (e.Node.Tag.ToString()) {
                case "MailBoxRoot":
                    // メールボックスが選択された場合
                    SetListViewColumns("名前", "メールアドレス", "最終データ更新日", "データサイズ");
                    Invoke(SetMessage,  "メールボックス");
                    listMail.ContextMenuStrip = null;
                    break;
                case "ReceiveMailBox":
                    // 受信メールが選択された場合
                    SetListViewColumns("差出人", "件名", "受信日時", "サイズ");
                    Invoke(SetMessage, "受信メール");
                    listMail.ContextMenuStrip = menuListView;
                    break;
                case "SendMailBox":
                    // 送信メールが選択された場合
                    SetListViewColumns("宛先", "件名", "送信日時", "サイズ"); ;
                    Invoke(SetMessage, "送信メール");
                    listMail.ContextMenuStrip = menuListView;
                    break;
                case "DeleteMailBox":
                    // ごみ箱が選択された場合
                    SetListViewColumns("差出人または宛先", "件名", "受信日時または送信日時", "サイズ");
                    Invoke(SetMessage, "ごみ箱");
                    listMail.ContextMenuStrip = menuListView;
                    break;
                default:
                    break;
            }

            ClearInput();
        }

        private void menuAppExit_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void menuSetEnv_Click(object sender, EventArgs e)
        {
            var settingForm = new SettingForm();

            timerAutoReceive.Enabled = false;

            var ret = settingForm.ShowDialog();

            if (ret == DialogResult.OK) {
                SetTimer(settingForm.checkAutoGetMail.Checked, AccountInfo.getMailInterval);
            }

            listMail.ListViewItemSorter = null;
            UpdateListView();
            listMail.ListViewItemSorter = listViewItemSorter;
        }

        private void timerStatusTime_Tick(object sender, EventArgs e)
        {
            labelDate.Text = DateTime.Now.ToString("yy/MM/dd hh:mm:ss");
        }

        private void menuNewMail_Click(object sender, EventArgs e)
        {
            var newMailForm = new MailEditorForm
            {
                Owner = this,
                // TODO たぶんメール渡すだけでいい
                SendList = mailBox.Send.ToList()
            };

            newMailForm.Show();
        }

        private async void menuSendMail_Click(object sender, EventArgs e)
        {
            menuRecieveMail.Enabled = false;
            buttonRecieveMail.Enabled = false;
            buttonRecieveMail.Enabled = false;
            buttonSendMail.Enabled = false;

            await Task.Run(() => SendMail());
        }

        private async void menuRecieveMail_Click(object sender, EventArgs e)
        {
            menuRecieveMail.Enabled = false;
            buttonRecieveMail.Enabled = false;

            await Task.Run(() => RecieveMail());
        }

        private void menuDeleteMail_Click(object sender, EventArgs e)
        {
            DeleteMail();
        }

        // 現在リストビューに表示されているメールボックスを取得。
        private MailFolder GetShowingMailFolder()
        {
            switch (listMail.Columns[0].Text) {
                case "差出人":
                    return mailBox.Receive;
                case "宛先":
                    return mailBox.Send;
                case "差出人または宛先":
                    return mailBox.Trash;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 未読メールを既読にする
        /// </summary>
        private void menuAlreadyRead_Click(object sender, EventArgs e)
        {
            ChangeSelectedMailReadStatus(false);
        }

        /// <summary>
        /// 既読メールを未読にする
        /// </summary>
        private void menuNotReadYet_Click(object sender, EventArgs e)
        {
            ChangeSelectedMailReadStatus(true);
        }

        private void ChangeSelectedMailReadStatus(bool unread)
        {
            var sList = GetShowingMailFolder();

            var items = listMail.SelectedItems;
            if (items.Count == 0) return;

            items.Cast<ListViewItem>()
                .Select(t => int.Parse(t.Name))
                .Select(i => sList[i])
                .ToList()
                .ForEach(m => m.NotReadYet = unread);

            ReforcusListView(listMail);

            dataModified = true;
        }

        private void listMail_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            currentRow = e.ItemIndex;
        }

        private void listMail_DoubleClick(object sender, EventArgs e)
        {
            // メールボックスのときは反応しない
            if (AccountSelected()) return;
            var item = listMail.SelectedItems[0];
            var mail = GetSelectedMail(item.Tag);

            EditMail(mail, item);
        }

        private async void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (Directory.Exists(TempFileRoot)) {
                Directory.Delete(TempFileRoot, true);
            }

            if (dataModified) {
                if (File.Exists(MailDataPath)) {
                    File.Delete(MailDataPath);
                }

                await Task.Run(() => mailBox.MailDataSave());
            }

            SaveSettings();
        }

        private void CheckSocket()
        {
            try {
                nMail.Winsock.Initialize();
            }
            catch (Exception exp) {
                // 64bit環境で32bit用のnMail.dllを使用して起動したときはエラーになる
                if (exp.Message.Contains("間違ったフォーマットのプログラムを読み込もうとしました。")) {
                    MessageBox.Show(MainFormMessages.Error.Needx64nMail, MainFormMessages.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Idle -= Application_Idle;
                    Application.Exit();
                }
            }
        }

        private void SetTimer(bool isEnabled, int intervalMinutes)
        {
            // 60,000(msec)
            timerAutoReceive.Interval = intervalMinutes * 60000;
            timerAutoReceive.Enabled = isEnabled;
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            // スプラッシュスクリーンよりも先にフォームが出ることがあるらしい
            this.Hide();
            var splash = new SplashScreen();
            splash.Initialize();

            var load = Task.Run(() => LoadSettings());
            CheckSocket();
            Options.EnableSaveHtmlFile();

            // ファイル展開用のテンポラリフォルダの作成
            if (!Directory.Exists(TempFileRoot)) {
                Directory.CreateDirectory(TempFileRoot);
            }
            await load;
            try {
                var t = Task.Run(() => mailBox.MailDataLoad());
                splash.ProgressMesssage = MainFormMessages.Notification.MailLoading;
                await t;
            }
            catch (MailLoadException ex) {
                Invoke(() =>
                {
                    MessageBox.Show("読み込みエラーが発生しました。\n メッセージ:" + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    dataModified = false;
                    Application.Exit();
                });
            }

            SetTimer(AccountInfo.autoMailFlag, AccountInfo.getMailInterval);

            splash.Dispose();

            if (!(AccountInfo.minimizeTaskTray && WindowState == FormWindowState.Minimized)) {
                ShowInTaskbar = true;
                this.Show();
            }

            listMail.ListViewItemSorter = ListViewItemComparer.Default;
            // このタイミングで初期化が走るらしい
            treeMailBoxFolder.ExpandAll();

            this.Activate();
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            Application.Idle -= Application_Idle;
            nMail.Winsock.Done();
        }

        private void MenuAction(Func<bool> CancelCondition, Action<Mail> action)
        {
            if (CancelCondition()) return;
            action(GetSelectedMail(listMail.SelectedItems[0].Tag));
        }

        private void menuReplyMail_Click(object sender, EventArgs e)
        {
            // 表示機能はシンプルなものに変わる
            MenuAction(() => listMail.SelectedItems.Count == 0, CreateReturnMail);
        }

        private void listMail_Click(object sender, EventArgs e)
        {
            MenuAction(AccountSelected, OpenMail);
        }

        private void menuGetAttatch_Click(object sender, EventArgs e)
        {
            // 送信メール以外も展開できるように変更
            MenuAction(() => listMail.SelectedItems.Count == 0, GetAttachMail);
        }

        private void menuSaveMailFile_Click(object sender, EventArgs e)
        {
            MenuAction(() => listMail.SelectedItems.Count == 0, mail => 
            {
                // ファイル名にメールの件名を入れる
                saveFileDialog1.FileName = mail.Subject;

                // 名前を付けて保存
                if (saveFileDialog1.ShowDialog() != DialogResult.OK) return;
                if (string.IsNullOrWhiteSpace(saveFileDialog1.FileName)) return;
                try {
                    SaveMailFile(mail, saveFileDialog1.FileName);
                }
                catch (Exception ex) {
                    MessageBox.Show(MainFormMessages.Error.GeneralErrorMessage(ex.Message), "エラー", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                }
            });
        }

        private void menuClearTrush_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(MainFormMessages.Check.ClearTrash, "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
                mailBox.Trash.Clear();
                ClearInput();
                dataModified = true;
            }
        }

        private void menuAbout_Click(object sender, EventArgs e)
        {
            AboutForm AboutForm = new AboutForm();
            AboutForm.ShowDialog();
        }

        private void buttonAttachList_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            var mail = GetSelectedMail(listMail.SelectedItems[0].Tag);

            // ファイルを開くかの確認をする
            if (MessageBox.Show(MainFormMessages.Check.OpenUnsafeFile(e.ClickedItem.Text), "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) {
                // 受信されたメールのとき
                if (mail.Attach.Length == 0) {
                    System.Diagnostics.Process.Start(TempFileRoot + "\\" + e.ClickedItem.Text);
                }
                else {
                    // 送信メールのとき
                    System.Diagnostics.Process.Start(e.ClickedItem.Text);
                }
            }
        }

        private void listMail_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            // メールボックスのときはソートしない
            if (AccountSelected()) return;

            listViewItemSorter.Column = e.Column;
            listMail.Sort();
        }

        private void timerAutoReceive_Tick(object sender, EventArgs e)
        {
            menuRecieveMail_Click(sender, e);
        }

        private void browserBody_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            browserBody.AllowNavigation = false;
        }

        private void MainForm_ClientSizeChanged(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized && AccountInfo.minimizeTaskTray) {
                // 最初化時に通知領域にしか表示しない
                this.Hide();
                notifyIcon1.Visible = true;
            }
        }

        private void menuTaskRestoreWindow_Click(object sender, EventArgs e)
        {
            this.ShowInTaskbar = true;
            this.Visible = true;

            if (this.WindowState == FormWindowState.Minimized) {
                this.WindowState = FormWindowState.Normal;
            }

            this.Activate();
        }

        private void menuFile_DropDownOpening(object sender, EventArgs e)
        {
            // メールボックスのメールを1件選んでいるとき
            var condition = listMail.SelectedItems.Count == 1 && !AccountSelected();
            menuSaveMailFile.Enabled = condition;
            menuGetAttatch.Enabled = condition;

            menuClearTrush.Enabled = mailBox.Trash.Count != 0;
        }

        private void menuMail_DropDownOpening(object sender, EventArgs e)
        {
            if (AccountSelected()) {
                menuDeleteMail.Enabled = false;
                menuReplyMail.Enabled = false;
                menuFowerdMail.Enabled = false;
            }
            else {
                menuDeleteMail.Enabled = listMail.SelectedItems.Count > 0;
                menuReplyMail.Enabled = listMail.SelectedItems.Count == 1;
                menuFowerdMail.Enabled = listMail.SelectedItems.Count == 1;
            }
        }

        private void menuListView_Opening(object sender, CancelEventArgs e)
        {
            // メール返信・転送・添付ファイル保存の有効/無効化
            var condition = !AccountSelected() && listMail.SelectedItems.Count == 1;
            menuContextReplyMail.Enabled = condition;
            menuContextFowerdMail.Enabled = condition;
            menuContextGetAttatch.Enabled = condition;

            // メールの未既読操作、削除の有効/無効化
            var mailCondition = listMail.SelectedItems.Count > 0 && !AccountSelected();
            menuContextDeleteMail.Enabled = mailCondition;
            menuNotReadYet.Enabled = mailCondition;
            menuAlreadyRead.Enabled = mailCondition;

            // ラベルの変更
            if (listMail.Columns[0].Text == "宛先") {
                menuAlreadyRead.Text = "送信済にする(&K)";
                menuNotReadYet.Text = "未送信にする(&U)";
            }
            else {
                menuAlreadyRead.Text = "既読にする(&K)";
                menuNotReadYet.Text = "未読にする(&U)";
            }
        }

        private void menuTreeView_Opening(object sender, CancelEventArgs e)
        {
            menuContextClearTrush.Enabled = mailBox.Trash.Count != 0;
        }

        private void Application_Idle(object sender, EventArgs e)
        {
            // ボタンの有効状態を変更
            if (AccountSelected()) {
                buttonDeleteMail.Enabled = false;
                buttonReplyMail.Enabled = false;
                buttonForwardMail.Enabled = false;
            }
            else {
                buttonDeleteMail.Enabled = listMail.SelectedItems.Count > 0;
                buttonReplyMail.Enabled = listMail.SelectedItems.Count == 1;
                buttonForwardMail.Enabled = listMail.SelectedItems.Count == 1;
            }
        }

        private void menuFowerdMail_Click(object sender, EventArgs e)
        {
            MenuAction(() => listMail.SelectedItems.Count == 0, CreateFowerdMail);
        }
        #endregion
    }
}