package uk.meshcore.command;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.bluetooth.*;
import android.bluetooth.le.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.Uri;
import android.os.*;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import android.view.View;
import android.webkit.*;
import android.widget.*;
import org.json.JSONObject;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.util.*;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** A single BLE companion transport for the trusted, bundled Command interface. */
public final class MainActivity extends Activity {
 private static final UUID SERVICE=UUID.fromString("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
 private static final UUID RX=UUID.fromString("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
 private static final UUID TX=UUID.fromString("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
 private static final UUID CCC=UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
 private static final String ORIGIN="appassets.androidplatform.net",CHANNEL="mesh_messages",KEY_ALIAS="meshcore-command-mobile-v1";
 private final Handler main=new Handler(Looper.getMainLooper());
 private final LinkedHashMap<String,BluetoothDevice> devices=new LinkedHashMap<>();
 private final ArrayDeque<Frame> frames=new ArrayDeque<>();
 private final Object frameLock=new Object(),writeLock=new Object();
 private BluetoothAdapter adapter; private BluetoothLeScanner scanner; private BluetoothGatt gatt;
 private BluetoothGattCharacteristic writeCharacteristic; private volatile boolean ready=false,scanning=false;
 private volatile int mtu=23,writeResult=-1; private volatile CountDownLatch writeLatch;
 private long sequence=0; private LinearLayout root; private TextView status; private Button scanButton; private ListView list; private ArrayAdapter<String> listAdapter; private WebView web;
 private BluetoothDevice connectingDevice; private boolean waitingForBond=false;
 private static final class Frame { final long seq; final byte[] bytes; Frame(long s,byte[] b){seq=s;bytes=b;} }

 @Override public void onCreate(Bundle state){super.onCreate(state);getWindow().setStatusBarColor(Color.rgb(7,16,24));getWindow().setNavigationBarColor(Color.rgb(7,16,24));
  BluetoothManager manager=(BluetoothManager)getSystemService(BLUETOOTH_SERVICE);adapter=manager==null?null:manager.getAdapter();
  if(Build.VERSION.SDK_INT>=26){NotificationChannel channel=new NotificationChannel(CHANNEL,"MeshCore messages",NotificationManager.IMPORTANCE_DEFAULT);getSystemService(NotificationManager.class).createNotificationChannel(channel);}
  if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},12);
  IntentFilter f=new IntentFilter(BluetoothDevice.ACTION_BOND_STATE_CHANGED);if(Build.VERSION.SDK_INT>=33)registerReceiver(bondReceiver,f,Context.RECEIVER_EXPORTED);else registerReceiver(bondReceiver,f);
  showConnect("Choose Scan for radios to find your MeshCore BLE companion.");
 }
 private TextView label(String text,int size){TextView v=new TextView(this);v.setText(text);v.setTextSize(size);v.setTextColor(Color.rgb(229,242,245));return v;}
 private void showConnect(String message){
  if(web!=null){web.removeJavascriptInterface("AndroidBridge");web.destroy();web=null;}
  root=new LinearLayout(this);root.setOrientation(LinearLayout.VERTICAL);root.setPadding(24,28,24,20);root.setBackgroundColor(Color.rgb(9,19,27));
  TextView title=label("MeshCore Command",24);root.addView(title);TextView sub=label("Android test version · Bluetooth",15);root.addView(sub);
  status=label(message,14);status.setPadding(0,24,0,20);root.addView(status);
  scanButton=new Button(this);scanButton.setText("Scan for radios");scanButton.setOnClickListener(v->startScan());root.addView(scanButton);
  TextView guide=label("The radio needs MeshCore BLE companion firmware. Keep the app open while using the mesh. Bluetooth pairing may ask for the code shown on the board.",13);guide.setPadding(0,16,0,16);root.addView(guide);
  list=new ListView(this);listAdapter=new ArrayAdapter<>(this,android.R.layout.simple_list_item_1,new ArrayList<>());list.setAdapter(listAdapter);list.setOnItemClickListener((parent,view,pos,id)->{ArrayList<BluetoothDevice> found=new ArrayList<>(devices.values());if(pos<found.size())connect(found.get(pos));});root.addView(list,new LinearLayout.LayoutParams(-1,0,1));setContentView(root);
 }
 private void setStatus(String message){main.post(()->{if(status!=null)status.setText(message);});}
 private boolean permissions(){if(Build.VERSION.SDK_INT>=31)return checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN)==PackageManager.PERMISSION_GRANTED&&checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)==PackageManager.PERMISSION_GRANTED;return checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION)==PackageManager.PERMISSION_GRANTED;}
 @SuppressLint("MissingPermission") private void startScan(){
  if(adapter==null){setStatus("This phone does not have Bluetooth LE.");return;}if(!permissions()){if(Build.VERSION.SDK_INT>=31)requestPermissions(new String[]{Manifest.permission.BLUETOOTH_SCAN,Manifest.permission.BLUETOOTH_CONNECT},10);else requestPermissions(new String[]{Manifest.permission.ACCESS_FINE_LOCATION},10);return;}
  if(!adapter.isEnabled()){startActivityForResult(new Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE),11);return;}
  stopScan();devices.clear();listAdapter.clear();scanner=adapter.getBluetoothLeScanner();if(scanner==null){setStatus("Bluetooth scanner unavailable.");return;}
  try{List<ScanFilter> filters=Collections.singletonList(new ScanFilter.Builder().setServiceUuid(new ParcelUuid(SERVICE)).build());scanner.startScan(filters,new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build(),scanCallback);scanning=true;scanButton.setEnabled(false);setStatus("Scanning for MeshCore radios…");main.postDelayed(()->{if(scanning){stopScan();setStatus(devices.isEmpty()?"No BLE companions found. Check the radio firmware and power, then scan again.":"Select a radio to connect.");}},15000);}catch(SecurityException e){setStatus("Bluetooth permission is required.");}
 }
 @SuppressLint("MissingPermission") private void stopScan(){if(scanning&&scanner!=null)try{scanner.stopScan(scanCallback);}catch(SecurityException ignored){}scanning=false;if(scanButton!=null)scanButton.setEnabled(true);}
 private final ScanCallback scanCallback=new ScanCallback(){@SuppressLint("MissingPermission") @Override public void onScanResult(int type,ScanResult result){BluetoothDevice d=result.getDevice();if(d==null)return;try{String key=d.getAddress(),name=d.getName();main.post(()->{if(!scanning||devices.containsKey(key))return;devices.put(key,d);listAdapter.add((name==null||name.isEmpty()?"MeshCore radio":name)+"\n"+key);setStatus("Select a radio to connect.");});}catch(SecurityException ignored){}}@Override public void onScanFailed(int error){main.post(()->{stopScan();setStatus("Bluetooth scan failed ("+error+").");});}};
 @SuppressLint("MissingPermission") private void connect(BluetoothDevice device){stopScan();closeRadio();connectingDevice=device;setStatus("Connecting and pairing. Follow any Bluetooth prompts on the phone.");try{gatt=device.connectGatt(this,false,gattCallback,BluetoothDevice.TRANSPORT_LE);if(gatt==null){setStatus("Could not start Bluetooth connection.");return;}BluetoothGatt attempt=gatt;main.postDelayed(()->{if(gatt==attempt&&!ready){closeRadio();setStatus("Connection timed out. Check the board and pairing code, then scan again.");}},45000);}catch(SecurityException e){setStatus("Bluetooth permission is required.");}}
 private final BroadcastReceiver bondReceiver=new BroadcastReceiver(){@SuppressLint("MissingPermission") @Override public void onReceive(Context context,Intent intent){if(!BluetoothDevice.ACTION_BOND_STATE_CHANGED.equals(intent.getAction()))return;BluetoothDevice d=intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE);if(d==null||connectingDevice==null||!d.equals(connectingDevice)||!waitingForBond)return;int state=intent.getIntExtra(BluetoothDevice.EXTRA_BOND_STATE,BluetoothDevice.ERROR);if(state==BluetoothDevice.BOND_BONDED&&d.getBondState()==BluetoothDevice.BOND_BONDED){waitingForBond=false;beginMtu();}else if(state==BluetoothDevice.BOND_NONE){waitingForBond=false;closeRadio();setStatus("Bluetooth pairing failed. Check the board's pairing code.");}}};
 @SuppressLint("MissingPermission") private void beginMtu(){BluetoothGatt current=gatt;if(current==null)return;try{if(!current.requestMtu(185)){closeRadio();setStatus("Could not negotiate Bluetooth frame size.");}}catch(SecurityException e){closeRadio();setStatus("Bluetooth permission is required.");}}
 private final BluetoothGattCallback gattCallback=new BluetoothGattCallback(){
  @SuppressLint("MissingPermission") @Override public void onConnectionStateChange(BluetoothGatt g,int code,int state){if(g!=gatt)return;if(code!=BluetoothGatt.GATT_SUCCESS||state==BluetoothProfile.STATE_DISCONNECTED){closeRadio();main.post(()->showConnect("Radio disconnected. Scan to reconnect."));return;}if(state==BluetoothProfile.STATE_CONNECTED){try{if(g.getDevice().getBondState()==BluetoothDevice.BOND_BONDED){beginMtu();}else{waitingForBond=true;setStatus("Pair with the radio using the code shown on its display.");if(!g.getDevice().createBond()){waitingForBond=false;closeRadio();setStatus("Could not start Bluetooth pairing.");}}}catch(SecurityException e){closeRadio();setStatus("Bluetooth permission is required.");}}}
  @SuppressLint("MissingPermission") @Override public void onMtuChanged(BluetoothGatt g,int value,int code){if(g!=gatt)return;if(code!=BluetoothGatt.GATT_SUCCESS||value<100){closeRadio();setStatus("The radio did not negotiate a suitable Bluetooth frame size.");return;}mtu=value;try{if(!g.discoverServices()){closeRadio();setStatus("Could not discover the companion service.");}}catch(SecurityException e){closeRadio();setStatus("Bluetooth permission is required.");}}
  @SuppressLint("MissingPermission") @Override public void onServicesDiscovered(BluetoothGatt g,int code){if(g!=gatt)return;BluetoothGattService service=g.getService(SERVICE);BluetoothGattCharacteristic receive=service==null?null:service.getCharacteristic(TX);writeCharacteristic=service==null?null:service.getCharacteristic(RX);BluetoothGattDescriptor descriptor=receive==null?null:receive.getDescriptor(CCC);if(code!=BluetoothGatt.GATT_SUCCESS||receive==null||writeCharacteristic==null||descriptor==null){closeRadio();setStatus("The selected device is not a supported MeshCore BLE companion.");return;}try{if(!g.setCharacteristicNotification(receive,true)){closeRadio();setStatus("Could not enable radio notifications.");return;}boolean sent;if(Build.VERSION.SDK_INT>=33)sent=g.writeDescriptor(descriptor,BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE)==BluetoothStatusCodes.SUCCESS;else{descriptor.setValue(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);sent=g.writeDescriptor(descriptor);}if(!sent){closeRadio();setStatus("Could not subscribe to the radio.");}}catch(SecurityException e){closeRadio();setStatus("Bluetooth permission is required.");}}
  @Override public void onDescriptorWrite(BluetoothGatt g,BluetoothGattDescriptor descriptor,int code){if(g!=gatt||!CCC.equals(descriptor.getUuid()))return;if(code!=BluetoothGatt.GATT_SUCCESS){closeRadio();setStatus("Radio notification setup failed ("+code+").");return;}ready=true;main.post(()->showRadio());}
  @Override public void onCharacteristicWrite(BluetoothGatt g,BluetoothGattCharacteristic c,int code){if(g!=gatt||!RX.equals(c.getUuid()))return;writeResult=code;CountDownLatch latch=writeLatch;if(latch!=null)latch.countDown();}
  @SuppressWarnings("deprecation") @Override public void onCharacteristicChanged(BluetoothGatt g,BluetoothGattCharacteristic c){if(Build.VERSION.SDK_INT<33&&g==gatt&&TX.equals(c.getUuid()))receive(c.getValue());}
  @Override public void onCharacteristicChanged(BluetoothGatt g,BluetoothGattCharacteristic c,byte[] data){if(g==gatt&&TX.equals(c.getUuid()))receive(data);}
 };
 private void receive(byte[] bytes){if(!ready||bytes==null||bytes.length<1||bytes.length>176)return; synchronized(frameLock){if(frames.size()>=512){main.post(()->{closeRadio();showConnect("Too many pending radio frames. Reconnect to avoid losing messages.");});return;}sequence=sequence>=0xffffffffL?1:sequence+1;frames.add(new Frame(sequence,bytes.clone()));}}
 @SuppressLint("MissingPermission") private void closeRadio(){ready=false;waitingForBond=false;writeCharacteristic=null;CountDownLatch latch=writeLatch;if(latch!=null)latch.countDown();BluetoothGatt old=gatt;gatt=null;connectingDevice=null;mtu=23;synchronized(frameLock){frames.clear();}if(old!=null)try{old.disconnect();old.close();}catch(SecurityException ignored){}}
 @SuppressLint({"SetJavaScriptEnabled","JavascriptInterface"}) private void showRadio(){if(!ready)return;status=null;scanButton=null;web=new WebView(this);web.setBackgroundColor(Color.rgb(7,16,24));WebSettings s=web.getSettings();s.setJavaScriptEnabled(true);s.setDomStorageEnabled(true);s.setUseWideViewPort(true);s.setLoadWithOverviewMode(false);s.setAllowFileAccess(false);s.setAllowContentAccess(false);s.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);s.setSupportMultipleWindows(false);web.addJavascriptInterface(new Bridge(),"AndroidBridge");web.setWebViewClient(new WebViewClient(){@Override public boolean shouldOverrideUrlLoading(WebView view,WebResourceRequest request){return !"https".equals(request.getUrl().getScheme())||!ORIGIN.equals(request.getUrl().getHost());}@Override public WebResourceResponse shouldInterceptRequest(WebView view,WebResourceRequest request){Uri u=request.getUrl();if("https".equals(u.getScheme())&&ORIGIN.equals(u.getHost())&&"/index.html".equals(u.getPath()))try{return new WebResourceResponse("text/html","UTF-8",getAssets().open("index.html"));}catch(IOException ignored){}if("https".equals(u.getScheme())&&"tile.openstreetmap.org".equals(u.getHost()))return null;return new WebResourceResponse("text/plain","UTF-8",404,"Not Found",Collections.emptyMap(),new ByteArrayInputStream(new byte[0]));}});web.setWebChromeClient(new WebChromeClient(){@Override public boolean onJsAlert(WebView v,String url,String message,JsResult result){new AlertDialog.Builder(MainActivity.this).setMessage(message).setPositiveButton("OK",(d,w)->result.confirm()).setOnCancelListener(d->result.cancel()).show();return true;}@Override public boolean onJsConfirm(WebView v,String url,String message,JsResult result){new AlertDialog.Builder(MainActivity.this).setMessage(message).setPositiveButton("OK",(d,w)->result.confirm()).setNegativeButton("Cancel",(d,w)->result.cancel()).setOnCancelListener(d->result.cancel()).show();return true;}@Override public boolean onJsPrompt(WebView v,String url,String message,String defaultValue,JsPromptResult result){EditText input=new EditText(MainActivity.this);input.setText(defaultValue);new AlertDialog.Builder(MainActivity.this).setMessage(message).setView(input).setPositiveButton("OK",(d,w)->result.confirm(input.getText().toString())).setNegativeButton("Cancel",(d,w)->result.cancel()).setOnCancelListener(d->result.cancel()).show();return true;}});setContentView(web);web.loadUrl("https://"+ORIGIN+"/index.html");}
 @Override public void onBackPressed(){if(web==null){super.onBackPressed();return;}web.evaluateJavascript("(()=>{const screen=document.querySelector('[data-screen=messages].active .messages-layout.mobile-chat-open');if(!screen)return false;screen.querySelector('.mobile-chat-back').click();return true})()",result->{if(!"true".equals(result))new AlertDialog.Builder(this).setTitle("Disconnect radio?").setMessage("Return to Bluetooth selection?").setNegativeButton("Cancel",null).setPositiveButton("Disconnect",(d,w)->{closeRadio();showConnect("Disconnected. Scan to connect again.");}).show();});}
 @Override protected void onDestroy(){stopScan();closeRadio();try{unregisterReceiver(bondReceiver);}catch(IllegalArgumentException ignored){}if(web!=null){web.removeJavascriptInterface("AndroidBridge");web.destroy();web=null;}super.onDestroy();}

 private final class Bridge {
  @JavascriptInterface public String readFrame(String acknowledged){if(!ready)return "{\"error\":true}";synchronized(frameLock){if(!frames.isEmpty()&&Long.toString(frames.peek().seq).equals(acknowledged))frames.remove();if(frames.isEmpty())return "{}";Frame f=frames.peek();try{return new JSONObject().put("seq",f.seq).put("data",Base64.encodeToString(f.bytes,Base64.NO_WRAP)).toString();}catch(Exception e){return "{\"error\":true}";}}}
  @SuppressLint("MissingPermission") @JavascriptInterface public boolean writeFrame(String encoded){byte[] data;try{data=Base64.decode(encoded,Base64.DEFAULT);}catch(IllegalArgumentException e){return false;}if(data.length<1||data.length>176)return false;synchronized(writeLock){BluetoothGatt current=gatt;BluetoothGattCharacteristic characteristic=writeCharacteristic;if(!ready||current==null||characteristic==null||data.length>mtu-3)return false;CountDownLatch latch=new CountDownLatch(1);writeLatch=latch;writeResult=-1;try{boolean started;if(Build.VERSION.SDK_INT>=33)started=current.writeCharacteristic(characteristic,data,BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT)==BluetoothStatusCodes.SUCCESS;else{characteristic.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT);characteristic.setValue(data);started=current.writeCharacteristic(characteristic);}if(!started)return false;boolean completed=latch.await(15,TimeUnit.SECONDS);if(!completed){ready=false;main.post(()->{if(gatt==current){closeRadio();showConnect("Bluetooth write timed out. Reconnect and check delivery before retrying.");}});}return completed&&ready&&current==gatt&&writeResult==BluetoothGatt.GATT_SUCCESS;}catch(SecurityException|InterruptedException e){if(e instanceof InterruptedException)Thread.currentThread().interrupt();return false;}finally{if(writeLatch==latch)writeLatch=null;}}}
  @JavascriptInterface public String credential(String raw){return credentialFor(raw,"secure_credentials");}
  @JavascriptInterface public String repeaterCredential(String raw){return credentialFor(raw,"repeater_credentials");}
  private String credentialFor(String raw,String storeName){try{if(raw==null||raw.length()>4096)throw new IllegalArgumentException();JSONObject request=new JSONObject(raw);String key=request.getString("key"),action=request.getString("action");if(!key.matches("[0-9a-f]{64}"))throw new IllegalArgumentException();android.content.SharedPreferences prefs=getSharedPreferences(storeName,MODE_PRIVATE);if("get".equals(action)){String saved=prefs.getString(key,null);return new JSONObject().put("password",saved==null?JSONObject.NULL:decrypt(saved)).toString();}if("save".equals(action)){String password=request.getString("password");if(password.length()>256||!prefs.edit().putString(key,encrypt(password)).commit())throw new IllegalStateException();return "{\"ok\":true}";}if("delete".equals(action)&&prefs.edit().remove(key).commit())return "{\"ok\":true}";throw new IllegalArgumentException();}catch(Exception e){return "{\"error\":true}";}}
  @JavascriptInterface public boolean copyText(String text){if(text==null||text.length()>4096)return false;android.content.ClipboardManager clipboard=(android.content.ClipboardManager)getSystemService(CLIPBOARD_SERVICE);if(clipboard==null)return false;clipboard.setPrimaryClip(ClipData.newPlainText("MeshCore Command",text));return true;}
  @JavascriptInterface public void notifyMessage(){if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)return;Notification.Builder builder=new Notification.Builder(MainActivity.this,CHANNEL).setSmallIcon(android.R.drawable.stat_notify_chat).setContentTitle("MeshCore Command").setContentText("A message has arrived.").setAutoCancel(true);getSystemService(NotificationManager.class).notify(1,builder.build());}
 }
 private SecretKey encryptionKey() throws Exception {KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);if(store.containsAlias(KEY_ALIAS))return (SecretKey)store.getKey(KEY_ALIAS,null);KeyGenerator generator=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");generator.init(new KeyGenParameterSpec.Builder(KEY_ALIAS,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build());return generator.generateKey();}
 private String encrypt(String value)throws Exception{Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,encryptionKey());return Base64.encodeToString(cipher.getIV(),Base64.NO_WRAP)+":"+Base64.encodeToString(cipher.doFinal(value.getBytes(StandardCharsets.UTF_8)),Base64.NO_WRAP);}
 private String decrypt(String value)throws Exception{String[] parts=value.split(":",-1);if(parts.length!=2)throw new IllegalArgumentException();Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,encryptionKey(),new GCMParameterSpec(128,Base64.decode(parts[0],Base64.DEFAULT)));return new String(cipher.doFinal(Base64.decode(parts[1],Base64.DEFAULT)),StandardCharsets.UTF_8);}
}
